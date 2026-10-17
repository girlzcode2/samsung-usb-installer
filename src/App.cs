using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Samsung USB Installer")]
[assembly: AssemblyDescription("Install Android APKs over an authorized USB connection")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace SamsungUsbInstaller
{
    public static class App
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var app = new Application();
                var controller = new InstallerWindow();
                if (args.Length == 2 && args[0] == "--preview")
                { controller.RenderPreviews(args[1]); return 0; }
                app.Run(controller.Window);
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length > 0)
                {
                    File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt"), error.ToString());
                    return 1;
                }
                MessageBox.Show("The installer could not start. Extract the whole ZIP to a folder and try again.\n\n" + error.Message,
                    "Samsung USB Installer", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    public sealed class InstallerWindow
    {
        public Window Window { get; private set; }
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly AdbService service;
        private readonly string adbPath;
        private DeviceSnapshot device = new DeviceSnapshot { State = ConnectionState.Disconnected };
        private string selectedApk;
        private bool polling, installing, closed;
        private string lastConnectionKey = "";
        private Button Choose { get { return Find<Button>("ChooseButton"); } }
        private Button Install { get { return Find<Button>("InstallButton"); } }
        private TextBlock Status { get { return Find<TextBlock>("InstallStatus"); } }

        public InstallerWindow()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            adbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-tools", "adb.exe");
            service = new AdbService(new ProcessAdbRunner(adbPath));
            Choose.Click += delegate { ChooseFile(); };
            Install.Click += async delegate { await InstallSelectedAsync(); };
            Find<Button>("RefreshButton").Click += async delegate { await RefreshAsync(); };
            Find<Button>("HelpButton").Click += delegate { ShowHelp(); };
            Find<Border>("FileDrop").PreviewDragOver += delegate(object sender, DragEventArgs e)
            {
                e.Effects = device.Ready && !installing && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            Find<Border>("FileDrop").Drop += delegate(object sender, DragEventArgs e)
            {
                if (!device.Ready || installing) return;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length == 1) SelectApk(files[0]);
                else SetStatus("Choose one standalone APK at a time.", false);
            };
            timer.Tick += async delegate { await RefreshAsync(); };
            Window.Loaded += async delegate { timer.Start(); await RefreshAsync(); };
            Window.Closing += OnClosing;
            Window.Closed += delegate { closed = true; timer.Stop(); lifetime.Cancel(); };
        }

        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private static SolidColorBrush Brush(string color) { return (SolidColorBrush)new BrushConverter().ConvertFromString(color); }

        private async Task RefreshAsync()
        {
            if (polling || installing || closed) return;
            polling = true;
            UpdateControls();
            try
            {
                if (!File.Exists(adbPath))
                {
                    ApplyDevice(new DeviceSnapshot { State = ConnectionState.Error, Detail = "The platform-tools folder is missing. Extract the entire ZIP, keeping platform-tools beside Main.exe." });
                    return;
                }
                var next = await service.DetectAsync(lifetime.Token);
                if (!closed) ApplyDevice(next);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            { if (!closed) ApplyDevice(new DeviceSnapshot { State = ConnectionState.Error, Detail = error.Message }); }
            finally { polling = false; if (!closed) UpdateControls(); }
        }

        private void ApplyDevice(DeviceSnapshot next)
        {
            device = next;
            string title, hint, pill;
            switch (next.State)
            {
                case ConnectionState.Ready:
                    title = "Your phone is connected"; hint = next.Model + " · USB authorized\nChoose an APK to install on this device."; pill = "CONNECTED VIA USB"; break;
                case ConnectionState.Unauthorized:
                    title = "Allow the connection"; hint = "Unlock your phone and tap Allow on its USB debugging prompt. Detection will continue automatically."; pill = "APPROVAL NEEDED"; break;
                case ConnectionState.Offline:
                    title = "Reconnect your device"; hint = "Your phone is offline. Unplug the cable, unlock the phone and reconnect it."; pill = "DEVICE OFFLINE"; break;
                case ConnectionState.Multiple:
                    title = "Connect one phone"; hint = "More than one USB device is connected. Disconnect the others so the app can select your phone."; pill = "MULTIPLE DEVICES"; break;
                case ConnectionState.Error:
                    title = "Connection needs attention"; hint = "Open Details below for the cause. Connection help has the setup steps and Samsung driver link."; pill = "CHECK CONNECTION"; break;
                default:
                    title = "Please connect device"; hint = "Connect your Samsung with a USB data cable. Unlock it and allow USB debugging."; pill = "WAITING FOR USB"; break;
            }
            Find<TextBlock>("ConnectionTitle").Text = title;
            Find<TextBlock>("ConnectionHint").Text = hint;
            Find<TextBlock>("StatusPill").Text = pill;
            Find<Ellipse>("StatusDot").Fill = Brush(next.Ready ? "#58E1BF" : "#FBBF66");
            Find<TextBlock>("PhoneSymbol").Text = next.Ready ? "✓" : "↓";
            string key = next.State + "|" + next.TransportId + "|" + next.Detail;
            if (key != lastConnectionKey)
            {
                lastConnectionKey = key;
                SetStatus(next.Ready ? (selectedApk == null ? "Connected. Choose an Android APK to continue." : "Ready to install your selected APK.") :
                    next.State == ConnectionState.Unauthorized ? "Approve this computer on your phone." :
                    next.State == ConnectionState.Disconnected ? "Connect your phone to get started." : hint, false);
                Log(title + (string.IsNullOrWhiteSpace(next.Detail) ? "" : "\n" + next.Detail));
            }
            UpdateControls();
        }

        private void UpdateControls()
        {
            Choose.IsEnabled = device.Ready && !installing;
            Install.IsEnabled = device.Ready && selectedApk != null && !installing && !polling;
            Find<CheckBox>("UpdateCheck").IsEnabled = !installing;
            Find<Button>("RefreshButton").IsEnabled = !installing;
        }

        private void ChooseFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose an Android APK", Filter = "Android application (*.apk)|*.apk|All files (*.*)|*.*",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(Window) == true) SelectApk(dialog.FileName);
        }

        private void SelectApk(string path)
        {
            try
            {
                long size;
                using (var file = ApkValidator.OpenValidated(path)) size = file.Length;
                selectedApk = System.IO.Path.GetFullPath(path);
                Find<TextBlock>("FileName").Text = System.IO.Path.GetFileName(path);
                Find<TextBlock>("FileName").ToolTip = selectedApk;
                Find<TextBlock>("FileMeta").Text = (size / 1048576.0).ToString("0.0") + " MB · Android APK";
                Choose.Content = "Choose another APK";
                SetStatus("Ready. Click download to install this APK on your phone.", false);
                Log("Selected " + System.IO.Path.GetFileName(path));
            }
            catch (Exception error)
            {
                selectedApk = null;
                Find<TextBlock>("FileName").Text = "No APK selected";
                Find<TextBlock>("FileMeta").Text = "Android .apk files only";
                Choose.Content = "Choose APK file";
                SetStatus(error is InvalidDataException ? error.Message : "Cannot read this file. Choose a complete Android APK.", false);
                Log(error.Message);
            }
            UpdateControls();
        }

        private async Task InstallSelectedAsync()
        {
            if (installing || polling || !device.Ready || selectedApk == null) return;
            string path = selectedApk, transport = device.TransportId;
            installing = true;
            UpdateControls();
            Install.Content = "Installing on your phone…";
            Find<ProgressBar>("Progress").Visibility = Visibility.Visible;
            SetStatus("Keep the cable connected. Larger APKs can take a few minutes.", false);
            Log("Installing " + System.IO.Path.GetFileName(path) + " on " + device.Model);
            try
            {
                var result = await service.InstallAsync(path, transport, Find<CheckBox>("UpdateCheck").IsChecked == true, lifetime.Token);
                if (closed) return;
                bool success = AdbService.WasInstalled(result);
                SetStatus(success ? "Installed successfully. You can open the app on your phone." : AdbService.ExplainInstallFailure(result), success);
                Log(result.Output.Length == 0 ? "Android returned no installation result." : result.Output);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!closed) { SetStatus("Installation could not complete: " + error.Message, false); Log(error.Message); }
            }
            finally
            {
                installing = false;
                if (!closed)
                {
                    Install.Content = "Download onto mobile device";
                    Find<ProgressBar>("Progress").Visibility = Visibility.Collapsed;
                    UpdateControls();
                }
            }
        }

        private void SetStatus(string text, bool success)
        { Status.Text = text; Status.Foreground = Brush(success ? "#087F68" : "#64748B"); }
        private void Log(string text)
        {
            var details = Find<TextBox>("Details");
            if (details.Text.Length > 16000) details.Text = details.Text.Substring(details.Text.Length - 8000);
            details.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            details.ScrollToEnd();
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (installing && MessageBox.Show(Window,
                "Installation is still in progress. Closing stops the PC's wait, but the phone may still finish installing. Close anyway?",
                "Installation in progress", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
        }

        private void ShowHelp()
        {
            var panel = new StackPanel { Margin = new Thickness(26) };
            panel.Children.Add(new TextBlock { Text = "Connect your Samsung", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
            panel.Children.Add(new TextBlock
            {
                Text = "1. On the phone: Settings → About phone → Software information. Tap Build number seven times and enter your screen-lock PIN if asked.\n\n" +
                    "2. Open Settings → Developer options and enable USB debugging. Keep your phone unlocked.\n\n" +
                    "3. Connect one phone with a USB data cable. Tap Allow in the USB debugging prompt on the phone. A charging-only cable will not work.\n\n" +
                    "4. Choose a standalone Android .apk file and click Download onto mobile device. Windows .exe files cannot run on Android.\n\n" +
                    "If no device appears, try another cable or USB port and install Samsung's official Windows USB driver. If the phone blocks debugging or installation, review its message; Auto Blocker or a device administrator may restrict it. This app cannot override those settings.",
                TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21
            });
            var driver = new Button { Content = "Open Samsung USB driver page", Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(14, 10, 14, 10), HorizontalAlignment = HorizontalAlignment.Left };
            driver.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://developer.samsung.com/android-usb-driver") { UseShellExecute = true }); }
                catch (Exception error) { MessageBox.Show(error.Message, "Could not open browser"); }
            };
            panel.Children.Add(driver);
            panel.Children.Add(new TextBlock { Text = "After use, you can turn USB debugging off.\nIndependent utility; not affiliated with Samsung or Google.", FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 20, 0, 0), TextWrapping = TextWrapping.Wrap });
            new Window
            {
                Owner = Window, Title = "Connection help", Width = 570, Height = 680,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F5F7FB"),
                FontFamily = new FontFamily("Segoe UI"), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            }.ShowDialog();
        }

        public void RenderPreviews(string directory)
        {
            // Offline visual checks only: does not start ADB or fabricate a live device.
            Directory.CreateDirectory(directory);
            ApplyDevice(new DeviceSnapshot { State = ConnectionState.Disconnected });
            AssertUi(!Choose.IsEnabled && !Install.IsEnabled, "Disconnected controls are disabled");
            Render(directory, "01-connect.png");
            ApplyDevice(new DeviceSnapshot { State = ConnectionState.Unauthorized });
            AssertUi(!Choose.IsEnabled && !Install.IsEnabled, "Authorization cannot be bypassed in the UI");
            Render(directory, "02-authorize.png");
            ApplyDevice(new DeviceSnapshot { State = ConnectionState.Ready, TransportId = "1", Model = "Samsung Galaxy · preview" });
            AssertUi(Choose.IsEnabled && !Install.IsEnabled, "Ready connection requires a selected APK");
            selectedApk = "example.apk";
            Find<TextBlock>("FileName").Text = "example-android-app.apk";
            Find<TextBlock>("FileMeta").Text = "24.6 MB · Android APK";
            SetStatus("Ready. Click download to install this APK on your phone.", false);
            UpdateControls();
            AssertUi(Choose.IsEnabled && Install.IsEnabled, "Ready APK enables install");
            Render(directory, "03-ready.png");
            installing = true; UpdateControls();
            AssertUi(!Choose.IsEnabled && !Install.IsEnabled, "Install cannot be duplicated or file changed");
            Install.Content = "Installing on your phone…";
            Find<ProgressBar>("Progress").Visibility = Visibility.Visible;
            SetStatus("Keep the cable connected. Larger APKs can take a few minutes.", false);
            Render(directory, "04-installing.png");
            installing = false; UpdateControls();
            Find<ProgressBar>("Progress").Visibility = Visibility.Collapsed;
            Install.Content = "Download onto mobile device";
            SetStatus("Installed successfully. You can open the app on your phone.", true);
            Render(directory, "05-success.png");
            Render(directory, "06-small-window.png", 888, 660);
            polling = true; UpdateControls();
            AssertUi(!Install.IsEnabled, "Poll prevents an overlapping installation");
            polling = false; UpdateControls();
            AssertUi(Install.IsEnabled, "Poll completion restores install control");
            ApplyDevice(new DeviceSnapshot { State = ConnectionState.Disconnected });
            AssertUi(!Install.IsEnabled, "Disconnect disables previously selected APK");
            File.WriteAllText(System.IO.Path.Combine(directory, "ui-checks.txt"), "8 UI state checks passed; 6 preview images rendered. These are simulated states, not hardware installation evidence.");
            lifetime.Cancel();
        }

        private static void AssertUi(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("UI check failed: " + message); }

        private void Render(string directory, string file, double width = 1008, double height = 720)
        {
            var content = (FrameworkElement)Window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(System.IO.Path.Combine(directory, file))) encoder.Save(stream);
        }
    }
}
