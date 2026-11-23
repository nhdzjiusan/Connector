namespace PeripheralPower;

internal interface IHidReportChannel
{
    void WriteOutput(byte[] report);
    byte[]? ReadInput();
    void SetFeature(byte[] report);
    byte[]? GetFeature(byte reportId);
}

internal static class HidRuleEngine
{
    internal static HidRuleResult Execute(HidRule rule, HidRuleDocument catalog, IHidReportChannel channel,
        Action<byte[], string>? onFrame = null)
    {
        if (rule.BuiltinDriver is null) return Execute(rule.Transaction, channel, onFrame: onFrame);

        // The receiver needs a successful availability probe before the battery exchange.
        // Both exchanges still use the same declarative parser and the same open channel.
        var phases = catalog.DriverSteps![rule.BuiltinDriver];
        var status = Execute(phases[0], channel, onFrame: onFrame);
        return status.Disposition == HidDisposition.Accepted
            ? Execute(phases[1], channel, onFrame: onFrame) : status;
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
        Action<byte[]>? onQuery = null, Action<byte[], string>? onFrame = null)
    {
        var query = BuildQuery(transaction.Query);
        var mode = transaction.Query.Mode;
        onQuery?.Invoke(query);
        if (mode == HidQueryMode.OutputThenInput) channel.WriteOutput(query);
        if (mode == HidQueryMode.FeatureSetGet) channel.SetFeature(query);

        for (int read = 0; read < transaction.Response.MaxReads; read++)
        {
            var frame = mode is HidQueryMode.FeatureSetGet or HidQueryMode.FeatureGetOnly
                ? channel.GetFeature(query[0]) : channel.ReadInput();
            if (frame is null) break;
            var result = Inspect(transaction, frame, out string reason);
            onFrame?.Invoke(frame, reason);
            if (result.Disposition != HidDisposition.NoMatch) return result;
        }
        return new(HidDisposition.NoMatch);
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
