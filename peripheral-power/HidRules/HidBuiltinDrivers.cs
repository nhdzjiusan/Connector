namespace PeripheralPower;

internal static class HidBuiltinDrivers
{
    internal const string G3Receiver = "mchose_g3_v2_receiver";

    internal static bool IsRegistered(string name) => name == G3Receiver;

    internal static bool ValidPhases(string name, HidTransaction[]? phases) => name == G3Receiver &&
        phases is [{ Parse: null, Response.AsleepWhen: not null }, { Parse: not null }];

    internal static HidRuleResult Execute(string name, HidTransaction[] phases, Func<HidTransaction, int, HidRuleResult> exchange)
    {
        if (!IsRegistered(name)) throw new InvalidOperationException("Unregistered HID driver.");
        var status = exchange(phases[0], 1);
        return status.Disposition == HidDisposition.Accepted ? exchange(phases[1], 2) : status;
    }
}
