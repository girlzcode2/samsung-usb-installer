using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SamsungUsbInstaller
{
    public sealed class CommandResult
    {
        public int ExitCode;
        public string Output;
        public bool TimedOut;
        public CommandResult(int code, string output, bool timedOut = false)
        { ExitCode = code; Output = output ?? ""; TimedOut = timedOut; }
    }

    public interface IAdbRunner
    {
        Task<CommandResult> RunAsync(string[] arguments, int timeoutMs, CancellationToken token);
    }

    public sealed class ProcessAdbRunner : IAdbRunner
    {
        private readonly string executable;
        public ProcessAdbRunner(string path) { executable = Path.GetFullPath(path); }

        // Windows CommandLineToArgvW / C runtime quoting. No shell ever sees these arguments.
        public static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        public Task<CommandResult> RunAsync(string[] arguments, int timeoutMs, CancellationToken token)
        {
            return Task.Run(delegate
            {
                token.ThrowIfCancellationRequested();
                var output = new StringBuilder();
                var gate = new object();
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(executable,
                        string.Join(" ", arguments.Select(Quote)))
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(executable)
                    };
                    // An unrelated developer shell must not redirect this app to a remote ADB server.
                    foreach (string name in new[] { "ADB_SERVER_SOCKET", "ANDROID_ADB_SERVER_ADDRESS",
                        "ANDROID_ADB_SERVER_PORT", "ANDROID_SERIAL" })
                        process.StartInfo.EnvironmentVariables.Remove(name);
                    DataReceivedEventHandler collect = delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (gate) if (output.Length < 32768) output.AppendLine(e.Data);
                    };
                    process.OutputDataReceived += collect;
                    process.ErrorDataReceived += collect;
                    process.Start();
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    var clock = Stopwatch.StartNew();
                    bool timedOut = false;
                    try
                    {
                        while (!process.WaitForExit(100))
                        {
                            token.ThrowIfCancellationRequested();
                            if (clock.ElapsedMilliseconds >= timeoutMs) { timedOut = true; break; }
                        }
                        token.ThrowIfCancellationRequested();
                        if (timedOut)
                        {
                            if (!process.HasExited) process.Kill();
                            process.WaitForExit(2000);
                        }
                        else
                        {
                            // ADB closes redirected pipes before its client exits; flush final Success/Failure.
                            process.WaitForExit();
                        }
                        lock (gate) return new CommandResult(timedOut ? -1 : process.ExitCode,
                            output.ToString().Trim(), timedOut);
                    }
                    finally
                    {
                        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                    }
                }
            }, token);
        }
    }

    public enum ConnectionState { Disconnected, Unauthorized, Offline, Multiple, Ready, Error }

    public sealed class DeviceSnapshot
    {
        public ConnectionState State;
        public string TransportId = "";
        public string Model = "Android phone";
        public string Detail = "";
        public bool Ready { get { return State == ConnectionState.Ready; } }
    }

    public sealed class AdbService
    {
        private readonly IAdbRunner runner;
        private string cachedTransport = "", cachedModel = "Android phone";
        public AdbService(IAdbRunner adbRunner) { runner = adbRunner; }

        public static DeviceSnapshot Failure(string message)
        {
            string lower = (message ?? "").ToLowerInvariant();
            var state = ConnectionState.Error;
            if (lower.Contains("unauthorized") || lower.Contains("still authorizing")) state = ConnectionState.Unauthorized;
            else if (lower.Contains("more than one")) state = ConnectionState.Multiple;
            else if (lower.Contains("offline")) state = ConnectionState.Offline;
            else if (lower.Contains("no devices") || lower.Contains("no device") || lower.Contains("device not found")) state = ConnectionState.Disconnected;
            return new DeviceSnapshot { State = state, Detail = message ?? "" };
        }

        public async Task<DeviceSnapshot> DetectAsync(CancellationToken token)
        {
            // -d deliberately selects USB only. Pin the resulting transport ID for every subsequent call.
            var transport = await runner.RunAsync(new[] { "-d", "transport-id" }, 10000, token);
            if (transport.TimedOut) return Failure("ADB did not respond. Reconnect the cable and try again.");
            if (transport.ExitCode != 0) return Failure(transport.Output);
            string id = transport.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (!Regex.IsMatch(id, "^[1-9][0-9]*$")) return Failure("ADB returned an invalid USB connection identifier.");
            var state = await runner.RunAsync(new[] { "-t", id, "get-state" }, 5000, token);
            if (state.ExitCode != 0) return Failure(state.Output);
            if (state.Output.Trim() != "device") return Failure("Device is " + state.Output + ". Start Android normally and reconnect.");
            if (cachedTransport != id)
            {
                var model = await runner.RunAsync(new[] { "-t", id, "shell", "getprop", "ro.product.model" }, 5000, token);
                if (model.ExitCode != 0) return Failure(model.Output);
                cachedModel = string.IsNullOrWhiteSpace(model.Output) ? "Android phone" : model.Output.Trim();
                cachedTransport = id;
            }
            return new DeviceSnapshot { State = ConnectionState.Ready, TransportId = id, Model = cachedModel };
        }

        public async Task<CommandResult> InstallAsync(string path, string transportId, bool update, CancellationToken token)
        {
            if (!Regex.IsMatch(transportId ?? "", "^[1-9][0-9]*$")) throw new InvalidOperationException("Reconnect your phone before installing.");
            // Hold a read-only file handle so the selected bytes cannot be changed during installation.
            using (var file = ApkValidator.OpenValidated(path))
            {
                var state = await runner.RunAsync(new[] { "-t", transportId, "get-state" }, 5000, token);
                if (state.ExitCode != 0 || state.Output.Trim() != "device")
                    return new CommandResult(1, "The selected USB connection is no longer ready. Reconnect your phone.\n" + state.Output);
                var args = new List<string> { "-t", transportId, "install", "--no-incremental" };
                if (update) args.Add("-r");
                args.Add(Path.GetFullPath(path));
                return await runner.RunAsync(args.ToArray(), 300000, token);
            }
        }

        public static bool WasInstalled(CommandResult result)
        {
            return !result.TimedOut && result.ExitCode == 0 && Regex.IsMatch(result.Output, @"(?m)^Success\s*$");
        }

        public static string ExplainInstallFailure(CommandResult result)
        {
            string text = result.Output;
            if (result.TimedOut) return "Installation timed out. The phone may still finish installing; check it before trying again.";
            if (text.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE")) return "This app has a different signature from the installed version. Get an APK from the same publisher. No app or data was removed.";
            if (text.Contains("INSTALL_FAILED_VERSION_DOWNGRADE")) return "The phone already has a newer version. Choose a newer APK.";
            if (text.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE")) return "There is not enough space on the phone. Free some storage, then try again.";
            if (text.Contains("INSTALL_FAILED_ALREADY_EXISTS")) return "This app is already installed. Enable app updates to install a matching newer version.";
            if (text.Contains("INSTALL_FAILED_NO_MATCHING_ABIS")) return "This APK is built for a different processor. Get the Android version for your phone.";
            if (text.Contains("INSTALL_FAILED_OLDER_SDK") || text.Contains("INSTALL_FAILED_DEPRECATED_SDK_VERSION")) return "This APK is incompatible with the phone's Android version. Get a compatible release from the publisher.";
            if (text.Contains("INSTALL_FAILED_MISSING_SPLIT")) return "This APK needs additional split files. Choose a standalone or universal APK; split bundles are not supported.";
            if (text.Contains("INSTALL_FAILED_USER_RESTRICTED") || text.Contains("INSTALL_CANCELED_BY_USER")) return "The phone blocked or declined installation. Review its on-screen message and security or administrator settings.";
            if (text.Contains("INSTALL_PARSE_FAILED") || text.Contains("INSTALL_FAILED_INVALID_APK")) return "Android rejected this APK. Download a complete, signed Android APK from its publisher.";
            if (text.ToLowerInvariant().Contains("offline") || text.ToLowerInvariant().Contains("not found") || text.Contains("no longer ready")) return "The USB connection was lost. Reconnect and authorize your phone, then try again.";
            return "Installation was not confirmed. Check the phone and open Details for the Android response.";
        }
    }

    public static class ApkValidator
    {
        public static FileStream OpenValidated(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose an Android .apk file. Windows .exe files cannot be installed on a Samsung phone.");
            var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (file.Length == 0) throw new InvalidDataException("The APK is empty. Download it again.");
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true))
                {
                    var manifest = zip.GetEntry("AndroidManifest.xml");
                    if (manifest == null || manifest.Length == 0)
                        throw new InvalidDataException("This file is not an Android APK: AndroidManifest.xml is missing or empty.");
                }
                file.Position = 0;
                return file;
            }
            catch { file.Dispose(); throw; }
        }
    }
}
