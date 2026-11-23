using PeripheralPower;

internal static class HidRuleChecks
{
    internal static void ValidateFile(string path, string id, string frameFile)
    {
        var rule = HidRuleLoader.LoadExternalFile(path).SingleOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Unknown rule ID: {id}");
        var frames = new List<byte[]>();
        foreach (var line in File.ReadLines(frameFile))
        {
            string hex = line.Split('#', 2)[0].Trim();
            if (hex.Length == 0) continue;
            if (frames.Count == 16 || hex.Length > HidRuleLoader.MaxReportLength * 3)
                throw new InvalidDataException("Replay requires at most 16 bounded response frames.");
            frames.Add(Convert.FromHexString(string.Concat(hex.Where(c => !char.IsWhiteSpace(c)))));
        }
        var result = HidRuleEngine.Execute(rule.Transaction, new Replay(frames),
            query => Console.WriteLine(query.Length == 0 ? "query: passive" : $"query: {Convert.ToHexString(query)}"),
            (frame, reason) => Console.WriteLine($"frame: {Convert.ToHexString(frame)} => {reason}"));
        Console.WriteLine(result.Sample is { } value
            ? $"result: {value.Percent}% power={System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.Power.ToString())} reference={value.IsReference}"
            : $"result: {result.Disposition}");
    }

    internal static void Run(Action<bool, string> check)
    {
        var empty = HidRuleLoader.LoadEmbedded();
        check(empty.Rules is not null, "load embedded HID rules");
        var status = new HidState(HidPower.Charging, [[new(2, Equal: 10, Mask: 0xF0, Shift: 4)]]);
        var parse = new HidParse(new(new(1, Mask: 0x7F), HidDecode.Percent), [status]);
        var transaction = new HidTransaction(
            new(HidQueryMode.OutputThenInput, 4, new() { [0] = 0x13, [2] = 3 },
                Checksum: new(HidChecksumKind.Sum8, 0, 3, 3)),
            new(3, 4, Expect: [[new(0, Equal: 0x13)]], AsleepWhen: [[new(2, Equal: 0xFF)]]), parse);
        var replay = new Replay([[0x13], [0x12, 23, 0], [0x13, 0xA8, 0xA1]]);
        var reasons = new List<string>();
        var result = HidRuleEngine.Execute(transaction, replay, onFrame: (_, reason) => reasons.Add(reason));
        check(replay.Writes.Single().SequenceEqual(new byte[] { 0x13, 0, 3, 0x16 }), "sparse frame preserves Report ID and writes sum8");
        check(result.Sample == new HidSample(40, HidPower.Charging) && reasons.SequenceEqual(["length", "expect", "accepted"]),
            "filter noisy replies and parse masked battery and shifted power");
        check(HidRuleEngine.Execute(transaction, new Replay([[0x13, 0, 0xFF]])).Disposition == HidDisposition.Asleep,
            "accepted sleep frame has no fresh battery value");
        check(HidRuleEngine.Checksum([0x0F, 0xF0], new(HidChecksumKind.Xor, 0, 2, 2)) == 0xFF &&
            HidRuleEngine.Checksum([0x0F, 0x01], new(HidChecksumKind.SubFromConst8, 0, 2, 2, Constant: 0x0A)) == 0xFA &&
            HidRuleEngine.Checksum("123456789"u8, new(HidChecksumKind.Crc8, 0, 9, 9)) == 0xF4 &&
            HidRuleEngine.Checksum([], new(HidChecksumKind.None)) == 0, "checksum variants and CRC-8 reference vector");

        var voltage = new HidTransaction(new(HidQueryMode.FeatureGetOnly, ReportId: 5), new(3),
            new(new(new(1, Width: 2), HidDecode.VoltageMv, Points: [new(3500, 0), new(3700, 30), new(4100, 100)])));
        var feature = new Replay([[5, 0x3C, 0x0F]]); // 3900 mV
        check(HidRuleEngine.Execute(voltage, feature).Sample?.Percent == 65 && feature.FeatureReads.SequenceEqual(new byte[] { 5 }) &&
            feature.Writes.Count == 0, "feature get-only and interpolated voltage");
        check(HidRuleEngine.Execute(voltage, new Replay([[5, 1, 0]])).Sample?.Percent == 0 &&
            HidRuleEngine.Execute(voltage, new Replay([[5, 0xFF, 0xFF]])).Sample?.Percent == 100, "voltage clamps to both ends");
        var setGet = voltage with { Query = new(HidQueryMode.FeatureSetGet, 3, new() { [0] = 5, [2] = 1 }) };
        feature = new Replay([[5, 0x3C, 0x0F]]);
        check(HidRuleEngine.Execute(setGet, feature).Sample?.Percent == 65 && feature.FeatureSet &&
            feature.FeatureReads.SequenceEqual(new byte[] { 5 }), "set/get feature preserves the Report ID");

        var passive = transaction with
        {
            Query = new(HidQueryMode.PassiveListen),
            Response = new(3, 1, Checksum: new(HidChecksumKind.Sum8, 0, 2, 2)),
            Parse = new(new(new(1), HidDecode.Lookup, Values: new() { [10] = 60 }))
        };
        replay = new Replay([[0, 10, 11], [0, 10, 10]]);
        check(HidRuleEngine.Execute(passive, replay).Disposition == HidDisposition.NoMatch && replay.InputReads == 1 &&
            replay.Writes.Count == 0, "passive mode checks response checksum and honors maxReads");
        check(HidRuleEngine.Execute(passive, new Replay([[0, 10, 10]])).Sample?.Percent == 60, "lookup reports mapped percent");
        var scale = passive with { Parse = new(new(new(1), HidDecode.Scale, Factor: 2.5, Offset: 1)) };
        check(HidRuleEngine.Execute(scale, new Replay([[0, 10, 10]])).Sample?.Percent == 26, "scale applies factor and offset");

        CheckLoader(check);
    }

    internal static void CheckScanIntegration(Action<bool, string> check)
    {
        var catalog = HidRuleLoader.LoadEmbedded();
        HidRule Rule(string id) => catalog.Rules.Single(rule => rule.Id == id);
        var wired = Rule("mchose-g3-v2-wired");
        var receiver = Rule("mchose-g3-v2-receiver");
        var keyboard = Rule("mchose-g87-v2-receiver");
        var headset = Rule("mchose-g9-pro-receiver");
        check(catalog.Rules.Length == 4 && wired.BuiltinDriver is null && keyboard.BuiltinDriver is null &&
            headset.BuiltinDriver is null && receiver.BuiltinDriver == HidRuleLoader.G3Receiver,
            "ship three declarative HID endpoints and one two-phase G3 receiver");

        byte[] g3Probe = [0, 0xAA, 0xED, 0, 0, 0, 0, 0, 0, 0];
        byte[] g3Battery = [0, 0xAA, 0x30, 0, 0, 0, 0, 0, 0, 12, 0];
        var radioChannel = new Replay([g3Probe, g3Battery]);
        var radioResult = HidRuleEngine.Execute(receiver, catalog, radioChannel);
        check(radioResult.Sample == new HidSample(12, HidPower.Discharging) && radioChannel.Writes.Count == 2,
            "receiver probes availability before querying battery on the same channel");
        byte[] fullWired = (byte[])g3Battery.Clone();
        fullWired[9] = 100;
        var wiredResult = HidRuleEngine.Execute(wired, catalog, new Replay([fullWired]));
        var wirelessReading = new UsbBattery.EndpointReading(receiver, radioResult.Sample!.Value);
        var wiredReading = new UsbBattery.EndpointReading(wired, wiredResult.Sample!.Value);
        foreach (UsbBattery.EndpointReading[] order in new[] { new[] { wiredReading, wirelessReading }, [wirelessReading, wiredReading] })
            check(UsbBattery.SelectReadings(order).Single() is
                { Id: "usb-Mouse", Percent: 100, Connection: "USB", FullOnPower: true }, "prefer wired priority regardless of completion order");
        check(UsbBattery.SelectReadings([wirelessReading]).Single() is { Id: "usb-Mouse", Percent: 12, Connection: "2.4 GHz" },
            "use receiver reading when wired has no fresh result");

        byte[] sleep = (byte[])g3Probe.Clone();
        sleep[9] = 1;
        var asleepChannel = new Replay([sleep, g3Battery]);
        var result = HidRuleEngine.Execute(receiver, catalog, asleepChannel);
        var last = new DateTime(2026, 11, 23, 9, 30, 0);
        var old = UsbBattery.SelectReadings([wirelessReading])[0] with { SampledAt = last };
        var held = PowerContext.Merge([old], "usb-", new([], [UsbBattery.ReadingId(receiver)], true),
            topologyChanged: true, last.AddMinutes(10)).Single();
        check(result.Disposition == HidDisposition.Asleep && result.Sample is null &&
            asleepChannel.Writes.Count == 1 && held is { Stale: true, Percent: 12 } && held.SampledAt == last,
            "sleep skips the battery command and keeps the group's last sample time");

        byte[] g87 = new byte[20];
        g87[0] = 0x13;
        g87[1] = 0x4A;
        g87[4] = 2;
        g87[5] = 80;
        g87[6] = 0x11;
        g87[19] = (byte)g87.Take(19).Sum(b => b);
        check(HidRuleEngine.Execute(keyboard, catalog, new Replay([g87])).Sample == new HidSample(80, HidPower.Full),
            "G87 checked 20-byte reply recognizes plugged full nibble");

        var g9 = new Replay([[0x55, 0x65, 1, 0], [0x55, 0x65, 8, 0]]);
        var g9Result = HidRuleEngine.Execute(headset, catalog, g9);
        check(g9Result.Sample == new HidSample(8, HidPower.Unknown, true) && g9.InputReads == 2 &&
            HidRuleEngine.Execute(headset, catalog, new Replay([[0x55, 0x65, 8, 3]])).Sample ==
                new HidSample(8, HidPower.Charging, true), "G9 rejects request echo and preserves uncertain and reference-charging states");

        var alerts = new Dictionary<string, int>();
        var unknown = UsbBattery.SelectReadings([new(headset, g9Result.Sample!.Value with { IsReference = false })]).Single();
        var reference = UsbBattery.SelectReadings([new(headset, new(8, HidPower.ExternalReference))]).Single();
        var discharging = UsbBattery.SelectReadings([new(headset, new(8, HidPower.Discharging))]).Single();
        check(PowerContext.CollectLowBatteryAlerts([unknown], alerts, true).Count == 0 &&
            PowerContext.CollectLowBatteryAlerts([reference], alerts, true).Count == 0 &&
            reference.PowerStatus == "参考电量 · 已接外部电源" &&
            PowerContext.CollectLowBatteryAlerts([held], alerts, true).Count == 0 && alerts.Count == 0 &&
            PowerContext.CollectLowBatteryAlerts([discharging], alerts, true).Count == 1 &&
            PowerContext.CollectLowBatteryAlerts([discharging], alerts, true).Count == 0,
            "suppress unknown, external reference and stale alerts while deduplicating confirmed low battery");

        var alias = UsbBattery.AddLegacyAliases(catalog, catalog, [new(4660, 22136, 0xFF010010, "Legacy mouse", DeviceKind.Mouse)])
            .Rules.Single(rule => rule.Match.Vid == 4660);
        check(alias.BuiltinDriver == receiver.BuiltinDriver && UsbBattery.ReadingId(alias) == "usb-1234-5678-FF010010",
            "legacy alias uses the built-in receiver and keeps its existing stable reading ID");
    }

    private const string Example = """
        {
          "id":"mock", "name":"Mock", "kind":"mouse",
          "match":{"vid":4660,"pids":[22136],"usagePage":65281,"usage":1,"deviceGroup":"mock","transport":"wired","priority":1},
          "query":{"mode":"output_then_input","length":3,"bytes":{"0":0,"1":1},"checksum":{"kind":"sub_from_const_8","start":0,"end":2,"index":2,"constant":10}},
          "response":{"minLength":2,"maxReads":1},
          "parse":{"level":{"field":{"index":1},"decode":"percent"}}
        }
        """;

    private static void CheckLoader(Action<bool, string> check)
    {
        string directory = Directory.CreateTempSubdirectory("PeripheralPower-HidRules-").FullName;
        try
        {
            string path = Path.Combine(directory, "00-base.json");
            File.WriteAllText(path, Example);
            var rule = HidRuleLoader.LoadExternalFile(path).Single();
            check(HidRuleEngine.BuildQuery(rule.Query!).SequenceEqual(new byte[] { 0, 1, 9 }) &&
                rule.Match.Matches(new(4660, 22136, 65281, 1, 2, 3, 0)), "strict generated JSON and exact match");

            File.WriteAllText(Path.Combine(directory, "10-override.json"), Example.Replace("\"name\":\"Mock\"", "\"name\":\"Override\""));
            File.WriteAllText(Path.Combine(directory, "20-unsafe.json"), Example.Replace("\"usagePage\":65281", "\"usagePage\":1"));
            var errors = new List<string>();
            var catalog = HidRuleLoader.LoadDirectory(directory, new([rule]), (file, _) => errors.Add(Path.GetFileName(file)));
            check(catalog.Rules.Single().Name == "Override" && errors.SequenceEqual(["20-unsafe.json"]),
                "valid override wins and unsafe override preserves the active rule");

            foreach (var (json, name) in new (string, string)[]
            {
                (Example.Replace("\"pids\":[22136]", "\"pids\":[0]"), "wildcard PID"),
                (Example.Replace("\"vid\":4660,\"pids\":[22136]", "\"vid\":5824,\"pids\":[1144]"), "known bootloader"),
                (Example.Replace("\"vid\":4660,\"pids\":[22136]", "\"vid\":1003,\"pids\":[8295]"), "known QMK bootloader"),
                (Example.Replace("\"usagePage\":65281", "\"usagePage\":65280"), "generic vendor usage page"),
                (Example.Replace("\"index\":1", "\"index\":2"), "out of bounds parse"),
                (Example.Replace("\"kind\":\"mouse\"", "\"kind\":\"mouse\",\"builtinDriver\":\"mchose_g3_v2_receiver\""), "external builtinDriver"),
                (Example.Replace("\"mode\":\"output_then_input\"", "\"mode\":0"), "numeric enum"),
                (Example.Replace("\"vid\":4660", "\"vid\":4660,\"vid\":2"), "duplicate property"),
                (Example.Replace("\"index\":1", "\"mask\":1"), "missing index")
            })
            {
                File.WriteAllText(path, json);
                bool rejected = false;
                try { HidRuleLoader.LoadExternalFile(path); }
                catch (Exception ex) when (HidRuleLoader.IsRuleError(ex)) { rejected = true; }
                check(rejected, $"reject {name}");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal sealed class Replay(IEnumerable<byte[]> frames) : IHidReportChannel
    {
        private readonly Queue<byte[]> queued = new(frames);
        public int InputReads { get; private set; }
        public List<byte[]> Writes { get; } = [];
        public List<byte> FeatureReads { get; } = [];
        public bool FeatureSet { get; private set; }
        public void WriteOutput(byte[] report) => Writes.Add(report);
        public byte[]? ReadInput() { InputReads++; return Next(); }
        public void SetFeature(byte[] report) { FeatureSet = true; Writes.Add(report); }
        public byte[]? GetFeature(byte reportId) { FeatureReads.Add(reportId); return Next(); }
        private byte[]? Next() => queued.TryDequeue(out var frame) ? frame : null;
    }
}
