using HidSharp;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeripheralPower;

internal enum DeviceKind { Mouse, Keyboard, Headset, Generic }

internal sealed record BatteryReading(string Id, string Name, DeviceKind Kind, int Percent, bool Charging,
    string Connection, bool IsReference = false, bool FullOnPower = false, DateTime? SampledAt = null, bool Stale = false)
{
    public string PowerStatus => Stale ? $"参考电量 · 上次更新 {SampledAt:HH:mm}" : FullOnPower ? "已充满 · 电源已连接" :
        Charging && IsReference ? "充电中 · 参考电量" : Charging ? "充电中" :
        IsReference ? "参考电量 · 供电状态未知" : "";
}

internal static class UsbBattery
{
    private readonly record struct BatterySample(int Percent, bool Charging, bool IsReference = false, bool FullOnPower = false);
    private sealed record Protocol(int Vendor, int Product, uint Usage, string Name, DeviceKind Kind)
    {
        public string Id => Builtins.Contains(this) ? $"usb-{Kind}" : $"usb-{Vendor:X4}-{Product:X4}-{Usage:X8}";
    }
    private static readonly Protocol[] Builtins =
    [
        new(0x3837, 0x418C, 0xFF010010, "MCHOSE G3 V2", DeviceKind.Mouse),
        new(0x3837, 0x4245, 0xFF010010, "MCHOSE G3 V2", DeviceKind.Mouse),
        new(0x3837, 0x201B, 0xFF020002, "MCHOSE G87 V2", DeviceKind.Keyboard),
        new(0x291D, 0x385D, 0xFF900001, "MCHOSE G9 Pro", DeviceKind.Headset)
    ];
    private static readonly Lazy<Protocol[]> Protocols = new(LoadProtocols);
    private static Protocol[] LoadProtocols()
    {
        string path = Path.Combine(Settings.Folder,"protocols.json");
        if (!File.Exists(path)) return Builtins;
        try
        {
            var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter<DeviceKind>(allowIntegerValues:false));
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("协议文件过大");
            var extra = System.Text.Json.JsonSerializer.Deserialize<Protocol[]>(File.ReadAllText(path),json)
                ?? throw new InvalidDataException("需要协议数组");
            if (extra.Length > 64 || extra.Any(p => p == null || p.Vendor is <1 or >65535 || p.Product is <1 or >65535 ||
                string.IsNullOrWhiteSpace(p.Name) || p.Kind == DeviceKind.Generic || p.Usage != (p.Kind switch
                    {DeviceKind.Mouse => 0xFF010010u, DeviceKind.Keyboard => 0xFF020002u, _ => 0xFF900001u})))
                throw new InvalidDataException("设备标识或 Usage 与内置协议不匹配");
            return Builtins.Concat(extra).DistinctBy(p => (p.Vendor,p.Product,p.Usage)).ToArray();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ErrorLog.Write("protocols.json",e); return Builtins; }
    }

    private static readonly SemaphoreSlim Enumeration = new(1);

    public static async Task<PollResult> ReadAsync(Action<string>? diagnostic = null)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task<BatteryReading?>[] tasks = [];
        HashSet<string>? present = null;
        bool expired = false;
        try
        {
            if (!Enumeration.Wait(0)) return new([], null, true);
            var inventory = Task.Run(() =>
            {
                try { return DeviceList.Local.GetHidDevices().ToArray(); }
                finally { Enumeration.Release(); }
            });
            ObserveFailure(inventory);
            var devices = await inventory.WaitAsync(budget.Token).ConfigureAwait(false);
            var matches = (from p in Protocols.Value from d in devices
                where d.VendorID == p.Vendor && d.ProductID == p.Product select (p,d)).ToArray();
            present = matches.Select(m => m.p.Id).ToHashSet();
            tasks = matches.Select(m => Task.Run(() =>
            {
                string path = m.d.DevicePath;
                if (budget.IsCancellationRequested) return null;
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
                using var gate = new Mutex(false, @"Local\PeripheralPower.Hid." + key);
                try { if (!gate.WaitOne(0)) return null; }
                catch (AbandonedMutexException ex) { ErrorLog.Write("回收 HID 设备锁",ex); }
                try { return Read(m.p, [m.d], diagnostic); }
                finally { gate.ReleaseMutex(); }
            })).ToArray();
            foreach (var task in tasks) ObserveFailure(task);
            await Task.WhenAll(tasks).WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { expired = true; }
        catch (Exception ex) { ErrorLog.Write("USB 设备扫描",ex); expired = true; }
        var values = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).OfType<BatteryReading>()
            .GroupBy(v => v.Id).Select(g => g.First()).ToArray();
        return new(values,present,expired || present?.Count > values.Length);
    }

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(t => ErrorLog.Write("HID 后台读取", t.Exception!.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

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
                    BatterySample? value = protocol.Kind switch
                    {
                        DeviceKind.Mouse => ReadMouse(device, stream, !(protocol.Vendor == 0x3837 && protocol.Product == 0x418C), diagnostic),
                        DeviceKind.Keyboard => ReadKeyboard(device, stream, diagnostic),
                        DeviceKind.Headset => ReadHeadset(device, stream, diagnostic),
                        _ => null
                    };
                    if (value is not { } battery) continue;

                    var name = protocol.Kind == DeviceKind.Headset ? device.GetProductName() : protocol.Name;
                    return new BatteryReading(protocol.Id, name, protocol.Kind,
                        battery.Percent, battery.Charging, protocol.Product == 0x418C ? "USB" : "2.4 GHz",
                        battery.IsReference, battery.FullOnPower);
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
            {
                diagnostic?.Invoke($"{protocol.Name}：{ex.Message}");
                if (ex is not TimeoutException and not NotSupportedException) ErrorLog.Write(protocol.Name,ex);
            }
        }
        return null;
    }

    // Windows includes the report ID byte even on the unnumbered G3 collection.
    private static BatterySample? ReadMouse(HidDevice device, HidStream stream, bool receiver, Action<string>? diagnostic)
    {
        if (receiver)
        {
            var status = Exchange(device, stream, [0, 0x55, 0xED],
                data => data.Length >= 10 && data[1] == 0xAA && data[2] == 0xED, diagnostic);
            // Receiver reports unavailable while sleeping or using a cable. The wired
            // endpoint wins if readable; otherwise the snapshot retains the previous sample.
            if (status == null || status[9] == 1) return null;
        }

        var reply = Exchange(device, stream, [0, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 1, 1, 1],
            data => data.Length >= 11 && data[1] == 0xAA && data[2] == 0x30 && data[9] <= 100, diagnostic);
        return reply == null ? null : MouseSample(reply[9], reply[10], receiver);
    }

    private static BatterySample MouseSample(int percent, byte status, bool receiver) =>
        new(percent, status == 1, FullOnPower: percent == 100 && (!receiver || status == 1));

    private static BatterySample? ReadKeyboard(HidDevice device, HidStream stream, Action<string>? diagnostic)
    {
        var request = new byte[20];
        request[0] = 0x13;
        request[1] = 0x4A;
        request[2] = 1;
        request[^1] = Checksum(request.AsSpan(0, request.Length - 1));
        var reply = Exchange(device, stream, request,
            data => data.Length == 20 && data[0] == 0x13 && data[1] == 0x4A &&
                data[4] >= 2 && data[5] <= 100 && Checksum(data.AsSpan(0, 19)) == data[19], diagnostic);
        return reply == null ? null : KeyboardSample(reply[5], reply[6]);
    }

    private static BatterySample KeyboardSample(int percent, byte status)
    {
        bool plugged = (status >> 4) == 1;
        bool full = plugged && ((status & 15) == 1 || percent == 100);
        return new BatterySample(percent, plugged && !full, FullOnPower: full);
    }

    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var value in data) sum += value;
        return unchecked((byte)sum);
    }

    private static BatterySample? ReadHeadset(HidDevice device, HidStream stream, Action<string>? diagnostic)
    {
        var reply = Exchange(device, stream, [0x55, 0x65, 1], IsHeadsetBatteryReply, diagnostic);
        if (reply == null) return null;
        // While plugged in, the headset can keep reporting the level from before charging.
        return HeadsetSample(reply[2], reply[3]);
    }

    private static BatterySample HeadsetSample(int percent, byte status) =>
        // Status zero has also been seen without independently confirmed USB power.
        // Keep its numeric report as a reference; only 3 and 4 confirm charging/full.
        new(percent, Charging: status == 3, IsReference: status is 0 or 3, FullOnPower: status == 4);

    private static bool IsHeadsetBatteryReply(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0x55 || data[1] != 0x65 || data[2] > 100) return false;
        // Status zero also occurs on USB power. Exclude empty reports and the 55 65 01 00 request echo.
        return data[3] is 2 or 3 or 4 || (data[3] == 0 && data[2] >= 2);
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
