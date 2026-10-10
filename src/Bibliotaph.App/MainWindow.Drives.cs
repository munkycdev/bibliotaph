using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

// Disks coming and going (slice 4g plan, choice 4). Windows tells every top-level window when a drive letter arrives
// or goes, so a library folder on a USB disk comes back as soon as the disk is plugged in, without Look for changes,
// and reads as offline as soon as it is pulled out. Network shares don't announce themselves; the indexing service
// looks at offline folders again every few minutes for those.
public partial class MainWindow
{
    const int WmDeviceChange = 0x0219;
    const int DbtDeviceArrival = 0x8000;
    const int DbtDeviceRemoveComplete = 0x8004;
    const int DbtDevTypVolume = 2;

    void WatchDrives() => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnDeviceChange);

    IntPtr OnDeviceChange(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmDeviceChange || lParam == IntPtr.Zero || wParam.ToInt64() is not (DbtDeviceArrival or DbtDeviceRemoveComplete)) return IntPtr.Zero;
        // DEV_BROADCAST_HDR: size, device type, reserved; a volume's DEV_BROADCAST_VOLUME then has its drive letters as a bit mask.
        if (Marshal.ReadInt32(lParam, 4) != DbtDevTypVolume) return IntPtr.Zero;
        var mask = (uint)Marshal.ReadInt32(lParam, 12);
        var drives = Enumerable.Range(0, 26).Where(i => (mask & (1u << i)) != 0).Select(i => (char)('A' + i)).ToList();
        _ = RescanDrivesAsync(drives);
        return IntPtr.Zero;
    }

    async Task RescanDrivesAsync(IReadOnlyCollection<char> drives)
    {
        try
        {
            await _indexing.RescanOfflineAsync(drives);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Looking at library folders after a drive change failed");
        }
    }
}
