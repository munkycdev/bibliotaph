using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Bibliotaph.App.Services;

/// <summary>
/// How much memory the computer's biggest graphics card has, so Settings > AI can suggest the model that fits (Built-in
/// AI plan, choice 5). Read from the display drivers' registry entries, which say it in full; WMI's AdapterRAM stops at
/// 4 GB.
/// </summary>
public sealed class GraphicsMemory(ILogger<GraphicsMemory> log)
{
    /// <summary>The display adapters' device class.</summary>
    const string Adapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    readonly Lazy<long?> _bytes = new(() => Read(log));

    /// <summary>The largest card's memory in bytes, or null when no driver says.</summary>
    public long? Bytes => _bytes.Value;

    static long? Read(ILogger log)
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(Adapters);
            if (adapters is null) return null;
            long? largest = null;
            foreach (var name in adapters.GetSubKeyNames().Where(n => n.All(char.IsAsciiDigit)))
            {
                using var adapter = adapters.OpenSubKey(name);
                if (adapter is null) continue;
                // A QWORD on current drivers; older ones give a DWORD, or the same four bytes as binary.
                var bytes = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => q,
                    _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int d => (long)(uint)d,
                        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
                        _ => (long?)null,
                    },
                };
                if (bytes > (largest ?? 0)) largest = bytes;
            }
            log.LogInformation("Graphics memory: {Bytes}", largest is { } found ? $"{found / (1L << 20):N0} MB" : "unknown");
            return largest;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            log.LogWarning(ex, "Could not read how much graphics memory there is");
            return null;
        }
    }
}
