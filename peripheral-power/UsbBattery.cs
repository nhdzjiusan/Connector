using HidSharp;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeripheralPower;

internal enum DeviceKind { Mouse, Keyboard, Headset }

internal sealed record BatteryReading(string Id, string Name, DeviceKind Kind, int Percent, bool Charging, string Connection);

internal static class UsbBattery
{
    private sealed record Protocol(int Vendor, int Product, uint Usage, string Name, DeviceKind Kind);
    private static readonly Protocol[] Protocols =
    [
        new(0x3837, 0x4245, 0xFF010010, "MCHOSE G3 V2", DeviceKind.Mouse),
        new(0x3837, 0x418C, 0xFF010010, "MCHOSE G3 V2", DeviceKind.Mouse),
        new(0x3837, 0x201B, 0xFF020002, "MCHOSE G87 V2", DeviceKind.Keyboard),
        new(0x291D, 0x385D, 0xFF900001, "MCHOSE G9 Pro", DeviceKind.Headset)
    ];

    public static async Task<BatteryReading[]> ReadAsync(Action<string>? diagnostic = null)
    {
        var devices = DeviceList.Local.GetHidDevices().ToArray();
        var tasks = Protocols.Select(protocol => Task.Run(() => Read(protocol, devices, diagnostic)));
        return (await Task.WhenAll(tasks)).OfType<BatteryReading>()
            .GroupBy(reading => reading.Id).Select(group => group.First()).ToArray();
    }

    private static BatteryReading? Read(Protocol protocol, HidDevice[] devices, Action<string>? diagnostic)
    {
        foreach (var device in devices.Where(d => d.VendorID == protocol.Vendor && d.ProductID == protocol.Product))
        {
            try
            {
                if (device.GetMaxOutputReportLength() == 0 ||
                    !device.GetReportDescriptor().DeviceItems.Any(item => item.Usages.GetAllValues().Contains(protocol.Usage)))
                    continue;

                if (!device.TryOpen(out var stream))
                {
                    diagnostic?.Invoke($"{protocol.Name} 打开失败");
                    continue;
                }

                using (stream)
                {
                    stream.ReadTimeout = 500;
                    stream.WriteTimeout = 550;
                    (int Percent, bool Charging)? value = protocol.Kind switch
                    {
                        DeviceKind.Mouse => ReadMouse(device, stream, protocol.Product == 0x4245, diagnostic),
                        DeviceKind.Keyboard => ReadKeyboard(device, stream, diagnostic),
                        DeviceKind.Headset => ReadHeadset(device, stream, diagnostic),
                        _ => null
                    };
                    if (value is not { } battery) continue;

                    var name = protocol.Kind == DeviceKind.Headset ? device.GetProductName() : protocol.Name;
                    return new BatteryReading($"usb-{protocol.Kind}", name, protocol.Kind,
                        battery.Percent, battery.Charging, protocol.Product == 0x418C ? "USB" : "2.4 GHz");
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
            {
                diagnostic?.Invoke($"{protocol.Name}：{ex.Message}");
            }
        }
        return null;
    }

    // Windows includes the report ID byte even on the unnumbered G3 collection.
    private static (int, bool)? ReadMouse(HidDevice device, HidStream stream, bool receiver, Action<string>? diagnostic)
    {
        if (receiver)
        {
            var status = Exchange(device, stream, [0, 0x55, 0xED],
                data => data.Length >= 10 && data[1] == 0xAA && data[2] == 0xED, diagnostic);
            if (status == null || status[9] == 1) return null;
        }

        var reply = Exchange(device, stream, [0, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 1, 1, 1],
            data => data.Length >= 11 && data[1] == 0xAA && data[2] == 0x30 && data[9] <= 100, diagnostic);
        return reply == null ? null : (reply[9], reply[10] == 1);
    }

    private static (int, bool)? ReadKeyboard(HidDevice device, HidStream stream, Action<string>? diagnostic)
    {
        var request = new byte[20];
        request[0] = 0x13;
        request[1] = 0x4A;
        request[2] = 1;
        request[^1] = Checksum(request.AsSpan(0, request.Length - 1));
        var reply = Exchange(device, stream, request,
            data => data.Length == 20 && data[0] == 0x13 && data[1] == 0x4A &&
                data[4] >= 2 && data[5] <= 100 && Checksum(data.AsSpan(0, 19)) == data[19], diagnostic);
        return reply == null ? null : (reply[5], (reply[6] >> 4) == 1 && (reply[6] & 15) != 1);
    }

    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var value in data) sum += value;
        return unchecked((byte)sum);
    }

    private static (int, bool)? ReadHeadset(HidDevice device, HidStream stream, Action<string>? diagnostic)
    {
        var reply = Exchange(device, stream, [0x55, 0x65, 1],
            data => data.Length >= 4 && data[0] == 0x55 && data[1] == 0x65 &&
                data[2] <= 100 && data[3] is 2 or 3 or 4, diagnostic);
        return reply == null ? null : (reply[2], reply[3] == 3);
    }

    private static byte[]? Exchange(HidDevice device, HidStream stream, byte[] command,
        Func<byte[], bool> matches, Action<string>? diagnostic)
    {
        int length = device.GetMaxOutputReportLength();
        if (length < command.Length || device.GetMaxInputReportLength() == 0) return null;
        var request = new byte[length];
        command.CopyTo(request, 0);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 0)
            {
                try { stream.Write(request); }
                catch (Exception ex) when (ex is TimeoutException or IOException) { continue; }
            }
            else
            {
                // Some USB collections accept requests through the control endpoint.
                using var control = File.OpenHandle(device.DevicePath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite, FileOptions.Asynchronous);
                if (!HidD_SetOutputReport(control, request, request.Length)) stream.Write(request);
            }
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 670)
            {
                try
                {
                    var data = stream.Read();
                    diagnostic?.Invoke($"{device.ProductID:X4} {Convert.ToHexString(data.AsSpan(0, Math.Min(20, data.Length)))}");
                    if (matches(data)) return data;
                }
                catch (TimeoutException) { break; }
            }
        }
        return null;
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] report, int length);
}
