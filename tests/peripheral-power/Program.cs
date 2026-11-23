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
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
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
