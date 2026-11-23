using PeripheralPower;
using System.Drawing.Imaging;

internal static class Checks
{
    private static void Require(bool ok,string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--hid-rule", ..] && args.Length > 1)
        {
            try
            {
                if (args is ["--hid-rule", var rule, "--id", var id, "--frames", var frames])
                    HidRuleChecks.ValidateFile(rule, id, frames);
                else if (args is ["--hid-rule", var source, "--id", var ruleId, "--diagnostic", var file, "--endpoint", var endpoint])
                    HidRuleChecks.ValidateDiagnostic(source, ruleId, file, endpoint);
                else throw new InvalidDataException("Expected --hid-rule <json|builtin> --id <id> followed by --frames <hex-file> or --diagnostic <scan.json> --endpoint <id>.");
            }
            catch (Exception ex) when (HidRuleLoader.IsRuleError(ex) || ex is FormatException)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length > 1 || args is ["--hid-rule"])
        {
            Console.Error.WriteLine("Usage: PeripheralPower.Check [screenshot-directory | --hid-rule <json> --id <id> --frames <hex-file>]");
            Environment.ExitCode = 2;
            return;
        }
        HidRuleChecks.Run(Require);
        HidRuleChecks.CheckScanIntegration(Require);
        HidRuleChecks.CheckDiagnosticsAndOverrides(Require);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        RefreshChecks.Run(Require);
        var area = new Rectangle(-200, 0, 200, 40);
        var geometry = new object[,]
        {
            { 0, 50000, "root", false, new double[] {-200,0,200,40}, true },
            { 0, 0, "TrayNotifyWnd", new object(), new double[] {-50,0,50,40}, new object() },
            { 0, 50000, "Button", false, new double[] {-199.5,0,20,40}, true },
            { 0, 50000, "Button", true, new double[] {-100,0,20,40}, true },
            { 0, 50000, "Button", false, new double[] {10,0,20,40}, true },
            { 0, 0, "MSTaskSwWClass", false, new double[] {-200,0,150,40}, false }
        };
        Require(TaskbarGeometry.OccupiedBounds(geometry,area).SequenceEqual(
            new[] { new Rectangle(-50,0,50,40), new Rectangle(-200,0,21,40) }),
            "reserve visible buttons and notification area even without IsOffscreen");
        var t = new DateTime(2026,11,23,9,30,0);
        BatteryReading[] old = [new("usb-Mouse","MCHOSE G3 V2",DeviceKind.Mouse,74,false,"2.4 GHz",SampledAt:t)];
        BatteryReading[] Merge(PollResult p, bool changed) => PowerContext.Merge(old,"usb-",p,changed,t.AddMinutes(5)).ToArray();
        var held = Merge(new([],null,true),true).Single();
        Require(held.Stale && held.SampledAt == t && held.Percent == 74,"enumeration timeout retains last reading");
        Require(Merge(new([],[],false),false).Length == 1,"ordinary polling cannot remove a device");
        Require(Merge(new([],[],false),true).Length == 0,"confirmed removal drops the row");
        Require(!Merge(new(old,["usb-Mouse"],false),false).Single().Stale,"successful reply recovers");
        BatteryReading[] rows = [old[0], new("usb-Keyboard","MCHOSE G87 V2",DeviceKind.Keyboard,100,false,"2.4 GHz"),
            new("usb-Headset","MCHOSE G9 Pro",DeviceKind.Headset,62,true,"2.4 GHz",true)];
        foreach (bool dark in new[]{false,true})
        {
            using var panel = new BatteryPanel(dark);
            panel.SetNotifications(true);
            panel.SetData(rows,t,false);
            var list = panel.Controls.OfType<Panel>().Single();
            var card = list.Controls.OfType<DeviceCard>().First(c => c.Id == "usb-Mouse");
            panel.SetData([rows[0] with { Percent=73 },rows[1],rows[2]],t.AddMinutes(1),false);
            Require(ReferenceEquals(card,list.Controls.OfType<DeviceCard>().First(c=>c.Id=="usb-Mouse")), "retain card on refresh");
            if (args.Length == 1)
            {
                Directory.CreateDirectory(args[0]);
                panel.Open(null);
                Application.DoEvents();
                panel.Update();
                using var bitmap = new Bitmap(panel.Width,panel.Height);
                panel.DrawToBitmap(bitmap,panel.ClientRectangle);
                bitmap.Save(Path.Combine(args[0],dark ? "dark.png" : "light.png"),ImageFormat.Png);
            }
        }
    }
}
