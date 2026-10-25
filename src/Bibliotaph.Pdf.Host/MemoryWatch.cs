using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Bibliotaph.Pdf.Host;

/// <summary>
/// Linux has no job object, and an address-space limit would stop .NET itself, which reserves far more than it uses.
/// So the worker's resident memory is read from /proc a few times a second and the worker killed once it passes the
/// cap, as the job object would: a damaged PDF that makes PDFium allocate without end can't take the server with it.
/// </summary>
[SupportedOSPlatform("linux")]
sealed class MemoryWatch : IDisposable
{
    static readonly TimeSpan Every = TimeSpan.FromMilliseconds(100);

    readonly Process _process;
    readonly long _limitBytes;
    readonly string _statm;
    readonly Timer _timer;

    public MemoryWatch(Process process, long limitBytes)
    {
        _process = process;
        _limitBytes = limitBytes;
        _statm = $"/proc/{process.Id}/statm";
        _timer = new Timer(_ => Check(), null, Every, Every);
    }

    /// <summary>Whether the worker was killed for passing the cap.</summary>
    public bool Exceeded { get; private set; }

    void Check()
    {
        long resident;
        try
        {
            // statm: size resident shared text lib data dt, in pages.
            resident = long.Parse(File.ReadAllText(_statm).Split(' ')[1], CultureInfo.InvariantCulture) * Environment.SystemPageSize;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            return; // Exited: nothing left to watch.
        }
        if (resident <= _limitBytes) return;
        Exceeded = true;
        try
        {
            _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}
