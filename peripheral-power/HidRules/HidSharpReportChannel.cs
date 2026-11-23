using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HidSharp;
using HidSharp.Reports;
using Microsoft.Win32.SafeHandles;

namespace PeripheralPower;

internal sealed class HidSharpReportChannel(HidDevice device, HidStream stream, DeviceItem collection,
    Stopwatch scan, int budgetMs, CancellationToken cancellation, bool allowUndescribedOutput) : IHidReportChannel
{
    private readonly Stopwatch inputWindow = new();
    private byte[]? output;
    private bool usedControl;

    public void WriteOutput(byte[] report)
    {
        output = Pad(report, device.GetMaxOutputReportLength(), collection.OutputReports, allowUndescribedOutput);
        usedControl = false;
        try
        {
            stream.WriteTimeout = Timeout(550);
            stream.Write(output);
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is (TimeoutException or IOException))
        {
            WriteControl();
        }
        inputWindow.Restart();
    }

    public byte[]? ReadInput()
    {
        if (!inputWindow.IsRunning) inputWindow.Start();
        byte[]? data = ReadInputWindow();
        if (data is null && output is not null && !usedControl)
        {
            WriteControl();
            inputWindow.Restart();
            data = ReadInputWindow();
        }
        return data;
    }

    public void SetFeature(byte[] report)
    {
        stream.WriteTimeout = Timeout(550);
        stream.SetFeature(Pad(report, device.GetMaxFeatureReportLength(), collection.FeatureReports));
        cancellation.ThrowIfCancellationRequested();
    }

    public byte[] GetFeature(byte reportId)
    {
        stream.ReadTimeout = Timeout(500);
        var report = Pad([reportId], device.GetMaxFeatureReportLength(), collection.FeatureReports);
        stream.GetFeature(report);
        cancellation.ThrowIfCancellationRequested();
        return report;
    }

    private byte[]? ReadInputWindow()
    {
        int remaining = 670 - (int)inputWindow.ElapsedMilliseconds;
        if (remaining <= 0) return null;
        int capacity = device.GetMaxInputReportLength();
        if (capacity is < 1 or > HidRuleLoader.MaxReportLength)
            throw new NotSupportedException("Unsupported Input report size.");
        stream.ReadTimeout = Timeout(Math.Min(remaining, 500));
        try
        {
            var data = stream.Read();
            cancellation.ThrowIfCancellationRequested();
            return data;
        }
        catch (TimeoutException) { return null; }
    }

    private void WriteControl()
    {
        stream.WriteTimeout = Timeout(550);
        usedControl = true;
        using var control = File.OpenHandle(device.DevicePath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite, FileOptions.Asynchronous);
        _ = Timeout(550);
        // This native fallback can outlive the caller's wait on a stalled driver. The worker
        // keeps the device mutex until the native call returns, so a later scan cannot overlap.
        if (!HidD_SetOutputReport(control, output!, output!.Length))
        {
            stream.WriteTimeout = Timeout(550);
            stream.Write(output);
        }
    }

    private int Timeout(int limit)
    {
        cancellation.ThrowIfCancellationRequested();
        var remaining = budgetMs - scan.ElapsedMilliseconds;
        if (remaining <= 0) throw new OperationCanceledException(cancellation);
        return (int)Math.Min(limit, remaining);
    }

    private static byte[] Pad(byte[] report, int capacity, IEnumerable<Report> reports, bool allowUndescribed = false)
    {
        if (capacity < report.Length || capacity > HidRuleLoader.MaxReportLength)
            throw new NotSupportedException("Query does not fit the device's report length.");
        var descriptor = reports.FirstOrDefault(r => r.ReportID == report[0]);
        if (descriptor is null ? !allowUndescribed || reports.Any() : descriptor.Length < report.Length)
            throw new NotSupportedException("Report ID or length does not match the selected HID collection.");
        if (capacity == report.Length) return report;
        var padded = new byte[capacity];
        report.CopyTo(padded, 0);
        return padded;
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] report, int length);
}
