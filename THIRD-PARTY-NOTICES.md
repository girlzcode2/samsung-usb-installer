# Third-party tools

The portable distribution includes the unmodified Windows Android SDK Platform Tools 37.0.1 archive downloaded from Google's official distribution endpoint:

https://dl.google.com/android/repository/platform-tools-latest-windows.zip

Downloaded archive SHA-256:
`45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99`

The app invokes `platform-tools/adb.exe`. It does not invoke fastboot or disk-image tools. All upstream files are retained with their notices. See `platform-tools/NOTICE.txt` in the portable distribution for the complete upstream copyright and license notices, including Apache 2.0 and component licenses. The app's MIT license does not replace these terms.

Upstream documentation: https://developer.android.com/tools/releases/platform-tools

ADB source: https://android.googlesource.com/platform/packages/modules/adb/

The .NET Framework runtime is supplied by Windows, not bundled. The phone illustration is original vector artwork in the app's XAML. Samsung and Android names identify compatibility; no endorsement is implied.
