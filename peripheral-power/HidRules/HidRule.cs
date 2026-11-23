using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralPower;

internal enum HidQueryMode { OutputThenInput, FeatureSetGet, FeatureGetOnly, PassiveListen }
internal enum HidChecksumKind { None, Xor, Sum8, [JsonStringEnumMemberName("sub_from_const_8")] SubFromConst8, Crc8 }
internal enum HidDecode { Percent, Scale, Lookup, VoltageMv }
internal enum HidPower { Unknown, Discharging, Charging, Full, ExternalReference }
internal enum HidTransport { Wired, Receiver }
internal enum HidDisposition { NoMatch, Accepted, Asleep, Invalid }

internal sealed record HidRule(string Id, string Name, DeviceKind Kind, HidMatch Match,
    HidQuery? Query = null, HidResponse? Response = null, HidParse? Parse = null, string? BuiltinDriver = null)
{
    [JsonIgnore]
    public HidTransaction Transaction => new(Query!, Response!, Parse);
}

internal sealed record HidMatch(int Vid, int[] Pids, int UsagePage, int Usage, string DeviceGroup,
    HidTransport Transport, int Priority, int MinInput = 0, int MinOutput = 0, int MinFeature = 0)
{
    public bool Matches(HidEndpoint target) => Vid == target.Vid && Pids.Contains(target.Pid) &&
        UsagePage == target.UsagePage && Usage == target.Usage &&
        target.Input >= MinInput && target.Output >= MinOutput && target.Feature >= MinFeature;
}

internal readonly record struct HidEndpoint(int Vid, int Pid, int UsagePage, int Usage, int Input, int Output, int Feature);
internal sealed record HidTransaction(HidQuery Query, HidResponse Response, HidParse? Parse = null);
internal sealed record HidQuery(HidQueryMode Mode, int Length = 0, Dictionary<int, int>? Bytes = null,
    int? ReportId = null, HidChecksum? Checksum = null);
internal sealed record HidChecksum(HidChecksumKind Kind, int Start = 0, int End = 0, int Index = 0,
    int Constant = 0, int Polynomial = 7, int Initial = 0, int XorOut = 0);
internal sealed record HidResponse(int MinLength, int MaxReads = 1, int? ExactLength = null,
    HidTest[][]? Expect = null, HidTest[][]? AsleepWhen = null, HidChecksum? Checksum = null);
internal sealed record HidField(int Index, int Width = 1, bool BigEndian = false, int? Mask = null, int Shift = 0)
{
    public int Get(ReadOnlySpan<byte> frame)
    {
        int value = frame[Index];
        if (Width == 2)
            value = BigEndian ? (value << 8) | frame[Index + 1] : value | (frame[Index + 1] << 8);
        return (value & (Mask ?? 0xFFFF)) >> Shift;
    }
}

// Each inner group is AND; the outer array is OR.
internal sealed record HidTest(int Index, int? Equal = null, int? Min = null, int? Max = null,
    int? Mask = null, int Shift = 0)
{
    public bool Matches(ReadOnlySpan<byte> frame)
    {
        int value = (frame[Index] & (Mask ?? 0xFF)) >> Shift;
        return (Equal is null || value == Equal) && (Min is null || value >= Min) && (Max is null || value <= Max);
    }
}

internal sealed record HidParse(HidLevel Level, HidState[]? States = null, HidPower DefaultPower = HidPower.Unknown);
internal sealed record HidLevel(HidField Field, HidDecode Decode, double Factor = 1, double Offset = 0,
    Dictionary<int, int>? Values = null, HidVoltagePoint[]? Points = null);
internal sealed record HidVoltagePoint(int Millivolts, int Percent);
internal sealed record HidState(HidPower Power, HidTest[][] When, bool IsReference = false);
internal readonly record struct HidSample(int Percent, HidPower Power, bool IsReference = false);
internal readonly record struct HidRuleResult(HidDisposition Disposition, HidSample? Sample = null);
internal sealed record HidRuleDocument(HidRule[] Rules, Dictionary<string, HidTransaction[]>? DriverSteps = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HidRule))]
[JsonSerializable(typeof(HidRuleDocument))]
[JsonSerializable(typeof(LegacyHidAlias[]))]
internal sealed partial class HidRuleJsonContext : JsonSerializerContext
{
    internal static HidRuleJsonContext Strict { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        Converters =
        {
            EnumConverter<HidQueryMode>(), EnumConverter<HidChecksumKind>(), EnumConverter<HidDecode>(),
            EnumConverter<HidPower>(), EnumConverter<HidTransport>(), EnumConverter<DeviceKind>()
        }
    });

    private static JsonStringEnumConverter<T> EnumConverter<T>() where T : struct, Enum =>
        new(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);
}

internal sealed record LegacyHidAlias(int Vendor, int Product, uint Usage, string Name, DeviceKind Kind);
