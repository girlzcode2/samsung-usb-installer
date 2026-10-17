# Samsung USB Installer

A portable Windows app for installing a standalone Android APK on a Samsung phone over an authorized USB connection.

## Run the app

1. Download `SamsungUsbInstaller-Windows-x64.zip` and choose **Extract all** in Windows.
2. Open the extracted `SamsungUsbInstaller` folder and run **Main.exe**. Keep its `platform-tools` folder beside it.
3. Connect one phone with a USB **data** cable. The app shows a phone-and-cable illustration and **Please connect device** until a usable connection is detected.
4. Enable USB debugging and approve the computer on the phone (instructions below).
5. Click **Choose APK file**, choose a standalone `.apk`, then click **Download onto mobile device**. You can also drop one APK into the file panel after connecting.
6. Wait for **Installed successfully**. Android has installed the app; open it on your phone.

`Main.exe` runs on Windows. The file installed on the phone must be an Android **APK**, not a Windows **EXE**. Renaming an EXE to APK does not convert it. The installer cannot bypass the phone's authorization, app signatures, Auto Blocker, administrator restrictions, or Android compatibility checks.

Requirements: Windows 10/11 x64 with .NET Framework 4.8 or newer, a USB data cable, a signed compatible APK, and an unlocked phone with USB debugging enabled. Windows 10 version 1903 and newer normally includes .NET Framework 4.8 or later. This build is unsigned; only run a copy you trust. No administrator access is requested by Main.exe. Samsung's separate USB driver installer may require administrator access.

## Set up the phone

In **Settings → About phone → Software information**, tap **Build number** seven times and enter your screen-lock PIN if prompted. Return to **Settings → Developer options**, enable **USB debugging**, and connect the cable. Unlock the phone and approve the **Allow USB debugging?** prompt. Labels may vary with your One UI version.

If the phone is not detected, try another data cable or USB port. Install [Samsung's official Windows USB driver](https://developer.samsung.com/android-usb-driver) if needed. If USB debugging or installation is blocked, review the phone's security or administrator settings. [Samsung Auto Blocker](https://www.samsung.com/ie/support/mobile-devices/protect-your-galaxy-device-with-the-new-auto-blocker-feature/) can restrict USB commands and app installation. The app reports the restriction and does not change it.

You can turn USB debugging off after use. See [Android's official ADB documentation](https://developer.android.com/tools/adb) for USB authorization and app installation.

## Behaviour and limitations

- Discovers one USB device; wireless devices and emulators are excluded. If several USB phones are attached, disconnect the others.
- Uses the connection's ADB transport ID throughout installation. A different or reconnected phone is not silently substituted.
- Supports standalone/universal APKs. Split APKs, `.apks`, `.xapk`, `.aab`, and Windows `.exe` files are not supported.
- **Allow app updates** uses `adb install -r`, retaining the installed app's data when Android accepts a matching signature. This app never uninstalls a conflicting app or forces a downgrade.
- Basic APK archive checks catch obvious wrong files. Android performs signature and compatibility checks. The app is not a malware scanner.
- Shows an indeterminate progress bar while Android works; reports success only when ADB exits successfully with `Success`.
- Install commands time out after five minutes. A timeout or closing the window stops waiting on the PC; the phone may still finish. Check the phone before retrying.
- Connection and installation details remain in memory for this session. No analytics, account login, automatic APK downloads, rooting, flashing, or background startup is added.
- ADB uses its normal local server and authorization keys. The standard ADB server may remain running after the UI closes.
- Designed for Samsung; the installation protocol can also work on other Android phones that support authorized ADB.

## Build from source

No Visual Studio or extra NuGet dependencies are needed on a Windows x64 machine with .NET Framework 4.8 installed. `build.ps1` uses the built-in Framework C# compiler and WPF assemblies.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

To package the complete portable app, download Windows Platform Tools from [Google's official page](https://developer.android.com/tools/releases/platform-tools), then provide the downloaded ZIP:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test -Package -PlatformToolsZip C:\Downloads\platform-tools-latest-windows.zip
```

The build verifies the archive against `platform-tools.sha256` (reviewed Platform Tools **37.0.1**). Google's `latest` URL can change. If the hash differs, verify the newer official release, review it and update the pinned hash intentionally; do not disable verification. No third-party binaries are committed in the source repository. The distributed ZIP retains the official Platform Tools files and notices.

The executable and portable ZIP are written under `dist/`. Tests use simulated ADB responses and a harmless local child process, and render the actual WPF UI to `build/previews/`. They do not install anything on a real phone. A physical Samsung installation still needs a hardware acceptance test.

The included GitHub Actions workflow builds, tests and packages on Windows when run manually, when source changes are pushed, or for pull requests. It uploads a downloadable Actions artifact. This does not publish a public release.

## Project layout

- `src/App.cs` — WPF event handling, connection display, file picker, installation state and help.
- `src/MainWindow.xaml` — native layout and original vector phone-and-cable illustration.
- `src/Core.cs` — subprocess handling, USB detection, APK validation and installation.
- `tests/` — connection, installation, quoting, timeout and cancellation checks.
- `build.ps1` — reproducible local build and packaging entry point.

Independent utility; not affiliated with Samsung or Google. See `LICENSE` for this app and `THIRD-PARTY-NOTICES.md` for bundled tools.
