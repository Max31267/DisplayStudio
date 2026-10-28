# Third-party components

Display Studio distributes these unmodified libraries with its portable Windows build. Their licenses and upstream notices continue to apply.

| Component | License | Corresponding source / project |
|---|---|---|
| esptool 5.6.0, separate executable | GPL-2.0-or-later | https://github.com/espressif/esptool/tree/v5.6.0 |
| ESP-IDF v5.5 firmware components | Apache-2.0 and component licenses, including MIT FreeRTOS | https://github.com/espressif/esp-idf/tree/v5.5 |
| LibreHardwareMonitorLib 0.9.6 | Mozilla Public License 2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701 |
| DiskInfoToolkit 1.1.2 | Mozilla Public License 2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD 1.4.2 | Mozilla Public License 2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core 1.0.7 | Mozilla Public License 2.0 | https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0 |
| HidSharp 2.6.4 | See included HidSharp license | https://software.seekye.com/hidsharp |
| SkiaSharp / SkiaSharp.NativeAssets.Win32 4.153.1 | MIT and upstream Skia notices | https://github.com/mono/SkiaSharp |
| Mono.Posix.NETStandard 1.0.0 | Upstream Microsoft license | https://go.microsoft.com/fwlink/?linkid=869050 |
| Microsoft .NET, Windows runtime projections and System.* libraries | MIT and Microsoft third-party notices | https://github.com/dotnet/runtime |

Mozilla Public License 2.0 is included in `licenses/LibreHardwareMonitor-MPL-2.0.txt`. The corresponding library sources are available from the linked upstream repositories; packages and repository revisions are recorded in the NuGet package metadata. No changes to those library sources are part of this application. Additional license and notice texts for HidSharp, SkiaSharp, Skia and .NET are included in `licenses/`.

The release kit includes the complete upstream esptool v5.6.0 source archive and license in `Licenses/`. The app invokes the unmodified Windows executable as a separate process. ESP-IDF and FreeRTOS license files are included there as well; `Firmware/source.zip` contains this project's firmware sources and build settings.
