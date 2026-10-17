using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SamsungUsbInstaller;

public class ScriptedAdb : IAdbRunner
{
    public readonly Queue<CommandResult> Results = new Queue<CommandResult>();
    public readonly List<string[]> Calls = new List<string[]>();
    public Task<CommandResult> RunAsync(string[] args, int timeout, CancellationToken token)
    { Calls.Add(args); return Task.FromResult(Results.Dequeue()); }
}

public static class Tests
{
    private static int count;
    private static void Check(bool result, string label)
    { if (!result) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); count++; }
    private static void Reject(Action action, string label)
    {
        bool failed = false;
        try { action(); } catch (InvalidDataException) { failed = true; }
        Check(failed, label);
    }
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--probe")
        {
            try
            {
                var device = new AdbService(new ProcessAdbRunner(args[1])).DetectAsync(CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("Real bundled ADB connection probe: " + device.State + "\n" + device.Detail);
                return device.State == ConnectionState.Error ? 1 : 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("\n" + count + " checks passed. No real phone installation was performed."); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static async Task Run()
    {
        var token = CancellationToken.None;
        Check(AdbService.Failure("error: no devices/emulators found").State == ConnectionState.Disconnected, "No USB device");
        Check(AdbService.Failure("error: device unauthorized").State == ConnectionState.Unauthorized, "Phone authorization needed");
        Check(AdbService.Failure("error: device offline").State == ConnectionState.Offline, "Offline device");
        Check(AdbService.Failure("error: more than one device").State == ConnectionState.Multiple, "Multiple USB devices");
        Check(AdbService.Failure("server cannot start").State == ConnectionState.Error, "Unknown failures are visible");
        var fake = new ScriptedAdb();
        fake.Results.Enqueue(new CommandResult(0, "* daemon started successfully\n42"));
        fake.Results.Enqueue(new CommandResult(0, "device"));
        fake.Results.Enqueue(new CommandResult(0, "SM-S921B"));
        var service = new AdbService(fake);
        var device = await service.DetectAsync(token);
        Check(device.Ready && device.TransportId == "42" && device.Model == "SM-S921B", "USB discovery and model");
        Check(fake.Calls[0].SequenceEqual(new[] { "-d", "transport-id" }), "Discovery excludes wireless devices and emulators");
        Check(fake.Calls[1].SequenceEqual(new[] { "-t", "42", "get-state" }), "State uses pinned USB transport");
        fake.Results.Enqueue(new CommandResult(0, "42")); fake.Results.Enqueue(new CommandResult(0, "device"));
        Check((await service.DetectAsync(token)).Ready && fake.Calls.Count == 5, "Stable device avoids unnecessary model requests");
        var invalid = new ScriptedAdb(); invalid.Results.Enqueue(new CommandResult(0, "bad;identifier"));
        Check(!(await new AdbService(invalid).DetectAsync(token)).Ready, "Invalid transport rejected");
        var timeout = new ScriptedAdb(); timeout.Results.Enqueue(new CommandResult(-1, "", true));
        Check((await new AdbService(timeout).DetectAsync(token)).State == ConnectionState.Error, "Detection timeout is recoverable");

        var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
        Directory.CreateDirectory(directory);
        string apk = Path.Combine(directory, "demo Ω app & (test).apk");
        using (var stream = File.Create(apk))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("AndroidManifest.xml").Open())) writer.Write("test manifest; not an installable real app");
        using (var stream = ApkValidator.OpenValidated(apk)) Check(stream.Length > 0, "APK archive structure accepted");
        Reject(delegate { ApkValidator.OpenValidated("program.exe"); }, "Windows EXE rejected");
        string bad = Path.Combine(directory, "renamed.apk"); File.WriteAllText(bad, "MZ this is not a ZIP");
        Reject(delegate { ApkValidator.OpenValidated(bad); }, "Renamed non-APK rejected");
        string missing = Path.Combine(directory, "missing-manifest.apk");
        using (var stream = File.Create(missing)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        Reject(delegate { ApkValidator.OpenValidated(missing); }, "ZIP without manifest rejected");
        fake.Results.Enqueue(new CommandResult(0, "device")); fake.Results.Enqueue(new CommandResult(0, "Performing Streamed Install\nSuccess"));
        var result = await service.InstallAsync(apk, "42", true, token);
        Check(AdbService.WasInstalled(result), "Success requires Android acknowledgement");
        Check(fake.Calls.Last().SequenceEqual(new[] { "-t", "42", "install", "--no-incremental", "-r", Path.GetFullPath(apk) }), "Install pins original connection and passes APK as one argument");
        fake.Results.Enqueue(new CommandResult(0, "device")); fake.Results.Enqueue(new CommandResult(0, "Success"));
        await service.InstallAsync(apk, "42", false, token);
        Check(!fake.Calls.Last().Contains("-r"), "Updates only when selected");
        int before = fake.Calls.Count;
        fake.Results.Enqueue(new CommandResult(1, "device not found"));
        Check(!AdbService.WasInstalled(await service.InstallAsync(apk, "42", true, token)) && fake.Calls.Count == before + 1, "Disconnect prevents installation; no fallback to another phone");
        Check(!AdbService.WasInstalled(new CommandResult(0, "Failure [test]")), "Zero exit code without Success is not success");
        Check(!AdbService.WasInstalled(new CommandResult(1, "Success")), "Nonzero exit code is not success");
        Check(!AdbService.WasInstalled(new CommandResult(-1, "Success", true)), "Timeout is not success");
        Check(AdbService.ExplainInstallFailure(new CommandResult(1, "INSTALL_FAILED_UPDATE_INCOMPATIBLE")).Contains("signature"), "Signature conflict explained without uninstalling");
        Check(AdbService.ExplainInstallFailure(new CommandResult(1, "INSTALL_FAILED_MISSING_SPLIT")).Contains("standalone"), "Split APK limitation explained");
        Check(AdbService.ExplainInstallFailure(new CommandResult(1, "INSTALL_FAILED_USER_RESTRICTED")).Contains("blocked"), "Phone restrictions respected");
        Check(AdbService.ExplainInstallFailure(new CommandResult(-1, "", true)).Contains("may still finish"), "Timeout does not promise rollback");

        var process = new ProcessAdbRunner(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeAdb.exe"));
        string[] values = { "", "space here", "Ω unicode", "quote\"here", "C:\\trailing\\", "a&b;$(x) (file).apk", "slashes\\\\\"end" };
        var echoed = await process.RunAsync(values, 5000, token);
        // First argument is empty, represented by an empty line; split without dropping empties.
        string[] lines = echoed.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string[] decoded = lines.Select(x => Encoding.UTF8.GetString(Convert.FromBase64String(x))).ToArray();
        Check(echoed.ExitCode == 0 && decoded.SequenceEqual(values.Skip(1)), "Real Windows process preserves spaces, Unicode, quotes, backslashes and shell characters");
        var clock = Stopwatch.StartNew();
        var timed = await process.RunAsync(new[] { "sleep" }, 200, token);
        Check(timed.TimedOut && clock.ElapsedMilliseconds < 5000, "Hung process times out");
        using (var cancel = new CancellationTokenSource(200))
        {
            bool cancelled = false;
            try { await process.RunAsync(new[] { "sleep" }, 10000, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Closing can cancel an ADB client");
        }
    }
}
