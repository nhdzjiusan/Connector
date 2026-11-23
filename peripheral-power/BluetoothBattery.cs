using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PeripheralPower;

internal static class BluetoothBattery
{
    private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private static readonly string Selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);

    public static DeviceWatcher Watch(Action changed)
    {
        var watcher = DeviceInformation.CreateWatcher(Selector, [Appearance], DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, _) => changed();
        watcher.Removed += (_, _) => changed();
        watcher.Updated += (_, _) => { }; // Required by DeviceWatcher before Start.
        watcher.Start();
        return watcher;
    }

    public static async Task<PollResult> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var devices = await DeviceInformation.FindAllAsync(Selector, [Appearance], DeviceInformationKind.AssociationEndpoint)
                .AsTask(timeout.Token);
            var tasks = devices.Select(device => ReadDeviceAsync(device, timeout.Token));
            var values = (await Task.WhenAll(tasks)).OfType<BatteryReading>().ToArray();
            return new(values, devices.Select(info => "ble-" + info.Id).ToHashSet(), timeout.IsCancellationRequested);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException)
        {
            return PollResult.Failed("蓝牙枚举",ex);
        }
    }

    private static async Task<BatteryReading?> ReadDeviceAsync(DeviceInformation info, CancellationToken timeout)
    {
        var kind = Classify(info);
        if (kind == null) return null;

        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(timeout);
            if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
            var found = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(timeout);
            try
            {
                if (found.Status != GattCommunicationStatus.Success) return null;
                int? percent = null;
                foreach (var service in found.Services)
                {
                    var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached)
                        .AsTask(timeout);
                    if (chars.Status != GattCommunicationStatus.Success) continue;
                    foreach (var characteristic in chars.Characteristics)
                    {
                        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) continue;
                        var battery = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeout);
                        if (battery.Status != GattCommunicationStatus.Success || battery.Value.Length != 1) continue;
                        using var data = DataReader.FromBuffer(battery.Value);
                        var value = data.ReadByte();
                        if (value <= 100) percent = Math.Min(value, percent ?? 100);
                    }
                }
                return percent is { } level ? new BatteryReading("ble-" + info.Id,
                    info.Name, kind.Value, level, false, "蓝牙") : null;
            }
            finally
            {
                foreach (var service in found.Services) service.Dispose();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException)
        {
            ErrorLog.Write("蓝牙设备读取",ex);
            return null;
        }
    }

    private static DeviceKind? Classify(DeviceInformation info)
    {
        if (info.Properties.TryGetValue(Appearance, out var raw) && raw is ushort appearance)
        {
            if (appearance == 0x03C1) return DeviceKind.Keyboard;
            if (appearance == 0x03C2) return DeviceKind.Mouse;
            if (appearance >> 6 == 41) return DeviceKind.Headset;
        }
        if (info.Name.Contains("鼠标") || info.Name.Contains("mouse", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Mouse;
        if (info.Name.Contains("键盘") || info.Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Keyboard;
        if (info.Name.Contains("耳机") || new[] { "headset", "headphones", "earbuds", "airpods" }
            .Any(word => info.Name.Contains(word, StringComparison.OrdinalIgnoreCase))) return DeviceKind.Headset;
        return null;
    }
}
