using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using Windows.Foundation;

namespace PeripheralPower;

internal static class BluetoothBattery
{
    private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private static readonly string Selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);

    public static IDisposable Watch(Action changed) => new ConnectionWatcher(changed);

    private sealed class ConnectionWatcher : IDisposable
    {
        private readonly DeviceWatcher watcher = DeviceInformation.CreateWatcher(Selector, [Appearance], DeviceInformationKind.AssociationEndpoint);
        private readonly Action changed;
        private bool disposed;

        internal ConnectionWatcher(Action changed)
        {
            this.changed = changed;
            try
            {
                watcher.Added += Added;
                watcher.Removed += Removed;
                watcher.Updated += Updated;
                watcher.Start();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Added(DeviceWatcher sender, DeviceInformation info) => changed();
        private void Removed(DeviceWatcher sender, DeviceInformationUpdate info) => changed();
        private static void Updated(DeviceWatcher sender, DeviceInformationUpdate info) { }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Release(() => watcher.Added -= Added);
            Release(() => watcher.Removed -= Removed);
            Release(() => watcher.Updated -= Updated);
            Release(() =>
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
            });
        }
    }

    public static async Task<PollResult> ReadAsync(Notifications? notifications = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(notifications?.Lifetime ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        int generation = notifications?.Generation ?? 0;
        try
        {
            var devices = await DeviceInformation.FindAllAsync(Selector, [Appearance], DeviceInformationKind.AssociationEndpoint)
                .AsTask(timeout.Token);
            var present = devices.Select(info => "ble-" + info.Id).ToHashSet();
            if (notifications?.IsCurrent(generation) == false) return new([], null, true);
            notifications?.Prune(present);
            var tasks = devices.Select(device => ReadDeviceAsync(device, notifications, generation, timeout.Token));
            var values = (await Task.WhenAll(tasks)).OfType<BatteryReading>().ToArray();
            return new(values, present, timeout.IsCancellationRequested);
        }
        catch (OperationCanceledException) { return new([], null, true); }
        catch (Exception ex) when (IsBluetoothError(ex)) { return PollResult.Failed("蓝牙枚举", ex); }
    }

    private static async Task<BatteryReading?> ReadDeviceAsync(DeviceInformation info, Notifications? notifications,
        int generation, CancellationToken timeout)
    {
        string id = "ble-" + info.Id;
        BatterySession? temporary = null;
        try
        {
            if (notifications?.Find(id) is { } existing) return await existing.PollAsync(timeout);
            if (notifications?.CanOpen(id, generation) == false) return null;
            temporary = await BatterySession.OpenAsync(info, timeout);
            if (temporary is null) return null;
            var reading = await temporary.PollAsync(timeout);
            if (notifications?.IsCurrent(generation) == true)
            {
                try
                {
                    if (await temporary.SubscribeAsync(notifications, timeout) && notifications.Keep(id, temporary, generation))
                        temporary = null; // The monitor now owns the device and services.
                }
                catch (Exception ex) when (IsBluetoothError(ex)) { ErrorLog.Write("订阅蓝牙电量", ex); }
            }
            return reading;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (IsBluetoothError(ex)) { ErrorLog.Write("蓝牙设备读取", ex); return null; }
        finally { if (temporary is not null) await temporary.CloseAsync(); }
    }

    private static bool IsBluetoothError(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or ObjectDisposedException;

    // Catalog operations run on the GUI poll's synchronization context. ValueChanged can
    // run on another thread and only updates the bounded per-characteristic sample slots.
    internal sealed class Notifications(Action valueChanged, Action disconnected)
    {
        private readonly Dictionary<string, BatterySession> sessions = [];
        private readonly Dictionary<string, Task> retiring = [];
        private readonly CancellationTokenSource lifetime = new();
        private bool disposed;
        internal int Generation { get; private set; }
        internal CancellationToken Lifetime => lifetime.Token;

        internal async Task ResetAsync()
        {
            Generation++;
            Prune(null);
            await Task.WhenAll(retiring.Values.ToArray());
            ForgetClosedSessions();
        }

        internal void Prune(HashSet<string>? present)
        {
            ForgetClosedSessions();
            foreach (var id in sessions.Keys.Where(id => present?.Contains(id) != true || !sessions[id].IsConnected).ToArray())
            {
                sessions.Remove(id, out var session);
                retiring[id] = session!.CloseAsync();
            }
        }

        internal void PruneDisconnected()
        {
            ForgetClosedSessions();
            foreach (var id in sessions.Keys.Where(id => !sessions[id].IsConnected).ToArray())
            {
                sessions.Remove(id, out var session);
                retiring[id] = session!.CloseAsync();
            }
        }

        private void ForgetClosedSessions()
        {
            foreach (var id in retiring.Keys.Where(id => retiring[id].IsCompletedSuccessfully).ToArray())
                retiring.Remove(id);
        }

        internal BatterySession? Find(string id) => sessions.GetValueOrDefault(id);
        internal bool IsCurrent(int generation) => !disposed && generation == Generation;
        internal bool CanOpen(string id, int generation)
        {
            if (!IsCurrent(generation)) return false;
            if (!retiring.TryGetValue(id, out var previous)) return true;
            return previous.IsCompletedSuccessfully && retiring.Remove(id);
        }
        internal bool Keep(string id, BatterySession session, int generation) =>
            IsCurrent(generation) && session.IsConnected && sessions.TryAdd(id, session);
        internal void ValueChanged() => valueChanged();
        internal void Disconnected() => disconnected();

        internal BatteryReading[] TakeUpdates() =>
            sessions.Values.Select(session => session.TakeUpdate()).OfType<BatteryReading>().ToArray();

        internal async Task CloseAsync()
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            try { await ResetAsync(); }
            finally { lifetime.Dispose(); }
        }
    }

    internal sealed class BatterySession
    {
        private sealed class Level(GattCharacteristic characteristic)
        {
            internal GattCharacteristic Characteristic { get; } = characteristic;
            internal long Revision;
            internal int? Percent;
            internal TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? Handler;
        }

        private readonly BluetoothLEDevice device;
        private readonly IReadOnlyList<GattDeviceService> services;
        private readonly Level[] levels;
        private readonly BatteryReading identity;
        private readonly object gate = new();
        private Notifications? monitor;
        private bool disposed, disconnected, changed, watchingConnection;

        private BatterySession(BluetoothLEDevice device, IReadOnlyList<GattDeviceService> services,
            IEnumerable<GattCharacteristic> characteristics, DeviceInformation info)
        {
            this.device = device;
            this.services = services;
            levels = characteristics.Select(value => new Level(value)).ToArray();
            identity = new("ble-" + info.Id, info.Name, Classify(info), 0, false, "蓝牙");
        }

        internal bool IsConnected
        {
            get
            {
                lock (gate)
                    return !disposed && !disconnected;
            }
        }

        internal static async Task<BatterySession?> OpenAsync(DeviceInformation info, CancellationToken timeout)
        {
            var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(timeout);
            if (device is null) return null;
            IReadOnlyList<GattDeviceService> services = [];
            bool transferred = false;
            try
            {
                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
                var found = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(timeout);
                services = found.Services ?? [];
                if (found.Status != GattCommunicationStatus.Success) return null;
                var characteristics = new List<GattCharacteristic>();
                foreach (var service in services)
                {
                    var result = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached)
                        .AsTask(timeout);
                    if (result.Status == GattCommunicationStatus.Success && result.Characteristics is { } values)
                        characteristics.AddRange(values);
                }
                if (characteristics.Count == 0) return null;
                var session = new BatterySession(device, services, characteristics, info);
                transferred = true;
                return session;
            }
            finally
            {
                if (!transferred)
                {
                    foreach (var service in services) Release(service.Dispose);
                    Release(device.Dispose);
                }
            }
        }

        internal async Task<BatteryReading?> PollAsync(CancellationToken timeout)
        {
            bool read = false;
            foreach (var level in levels)
            {
                var characteristic = level.Characteristic;
                if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Read) == 0) continue;
                long before;
                lock (gate) before = level.Revision;
                int? percent = await ReadLevelAsync(characteristic, timeout);
                if (percent is null) continue;
                lock (gate)
                    if (!disposed && level.Revision == before) level.Percent = percent;
                read = true;
            }
            lock (gate) return read && !disposed && !disconnected ? CurrentReading() : null;
        }

        private static async Task<int?> ReadLevelAsync(GattCharacteristic characteristic, CancellationToken timeout)
        {
            foreach (var cache in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
            {
                GattReadResult battery;
                try { battery = await characteristic.ReadValueAsync(cache).AsTask(timeout); }
                catch (COMException ex) when (cache == BluetoothCacheMode.Cached)
                { ErrorLog.Write("读取蓝牙缓存", ex); continue; }
                if (battery.Status == GattCommunicationStatus.Success && ReadPercent(battery.Value) is int value)
                    return value;
            }
            return null;
        }

        internal async Task<bool> SubscribeAsync(Notifications notifications, CancellationToken timeout)
        {
            lock (gate) monitor = notifications;
            device.ConnectionStatusChanged += ConnectionChanged;
            watchingConnection = true;
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) return false;
            bool subscribed = false;
            foreach (var level in levels)
            {
                var characteristic = level.Characteristic;
                if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Notify) == 0) continue;
                if (timeout.IsCancellationRequested) break;
                TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (_, args) => Notify(level, args);
                characteristic.ValueChanged += handler;
                bool enabled = false;
                try
                {
                    var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(timeout);
                    enabled = result == GattCommunicationStatus.Success;
                    if (enabled) { level.Handler = handler; subscribed = true; }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (IsBluetoothError(ex)) { ErrorLog.Write("订阅蓝牙电量", ex); }
                finally { if (!enabled) characteristic.ValueChanged -= handler; }
            }
            return subscribed;
        }

        private void Notify(Level level, GattValueChangedEventArgs args)
        {
            try
            {
                if (ReadPercent(args.CharacteristicValue) is not int percent) return;
                Notifications? target;
                lock (gate)
                {
                    if (disposed || disconnected) return;
                    level.Revision++;
                    level.Percent = percent;
                    if (changed) return; // Keep only the latest value until the UI drains this session.
                    changed = true;
                    target = monitor;
                }
                target?.ValueChanged();
            }
            catch (Exception ex) when (IsBluetoothError(ex)) { ErrorLog.Write("接收蓝牙电量通知", ex); }
        }

        private void ConnectionChanged(BluetoothLEDevice sender, object args)
        {
            try
            {
                if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected) return;
                Notifications? target;
                lock (gate)
                {
                    if (disposed || disconnected) return;
                    disconnected = true;
                    target = monitor;
                }
                target?.Disconnected();
            }
            catch (Exception ex) when (IsBluetoothError(ex)) { ErrorLog.Write("蓝牙电量设备连接状态", ex); }
        }

        internal BatteryReading? TakeUpdate()
        {
            lock (gate)
            {
                if (!changed || disposed || disconnected) return null;
                changed = false;
                return CurrentReading();
            }
        }

        private BatteryReading? CurrentReading()
        {
            var percent = levels.Min(level => level.Percent);
            return percent is int level ? identity with { Percent = level } : null;
        }

        internal async Task CloseAsync()
        {
            bool wasConnected;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                wasConnected = !disconnected;
                monitor = null;
            }
            if (watchingConnection) Release(() => device.ConnectionStatusChanged -= ConnectionChanged);
            foreach (var level in levels)
                if (level.Handler is { } handler) Release(() => level.Characteristic.ValueChanged -= handler);
            try
            {
                if (!wasConnected || !levels.Any(level => level.Handler is not null) ||
                    device.ConnectionStatus != BluetoothConnectionStatus.Connected) return;
                using var timeout = new CancellationTokenSource(350);
                foreach (var level in levels)
                {
                    if (level.Handler is null) continue;
                    await level.Characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(timeout.Token).WaitAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException) { } // The local handlers are already detached.
            catch (Exception ex) when (IsBluetoothError(ex)) { ErrorLog.Write("关闭蓝牙电量通知", ex); }
            finally
            {
                foreach (var service in services) Release(service.Dispose);
                Release(device.Dispose);
            }
        }
    }

    private static void Release(Action release)
    {
        try { release(); }
        catch (Exception ex) when (IsBluetoothError(ex)) { ErrorLog.Write("关闭蓝牙电量监听", ex); }
    }

    internal static int? DecodePercent(uint length, byte value) => length == 1 && value <= 100 ? value : null;

    private static int? ReadPercent(IBuffer? buffer)
    {
        if (buffer?.Length != 1) return null;
        using var data = DataReader.FromBuffer(buffer);
        return DecodePercent(buffer.Length, data.ReadByte());
    }

    private static DeviceKind Classify(DeviceInformation info)
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
        return DeviceKind.Generic;
    }
}
