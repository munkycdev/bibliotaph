using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Bibliotaph.PdfWorker;

/// <summary>
/// On Linux, a worker the host asks to confine (<c>--confine &lt;folder&gt;</c>, once per folder it may read books
/// from) shuts itself in with Landlock: it can read its own program, the system's libraries and those folders, write
/// nowhere but its render buffer, start no program, open no TCP connection and signal no process outside itself. On the
/// web server it parses books anyone may have put in their storage, beside every user's library and the server's
/// secrets, so a PDF that took the worker over would find nothing to read and nowhere to send it.
/// <para>
/// Landlock confines only the thread that asks, and the runtime has started others by then, so the worker asks and then
/// starts again in the same process with execve: the new program's every thread is born inside. A kernel without
/// Landlock leaves the worker unconfined, and it says so.
/// </para>
/// </summary>
static partial class Confinement
{
    const string ConfineArg = "--confine";
    const string ConfinedArg = "--confined";

    /// <summary>What the worker says of itself in its first line on stderr and in answer to a ping; null when not asked.</summary>
    public static string? Status { get; private set; }

    /// <summary>
    /// Takes the confinement arguments out of <paramref name="args"/>. Asked to confine, on Linux, it doesn't return when
    /// it can: the confined worker starts in this one's place.
    /// </summary>
    public static string[] Enter(string[] args)
    {
        var folders = new List<string>();
        var rest = new List<string>();
        string? confined = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == ConfineArg && i + 1 < args.Length) folders.Add(args[++i]);
            else if (args[i] == ConfinedArg && i + 1 < args.Length) confined = args[++i];
            else rest.Add(args[i]);
        }

        if (confined is not null) Status = $"Confined by Landlock (ABI {confined}).";
        else if (folders.Count > 0)
            Status = OperatingSystem.IsLinux() ? $"Not confined: {Restart(folders, rest)}" : "Not confined: Landlock is Linux's.";
        return [.. rest];
    }

    /// <summary>Confines this thread and starts the worker again inside; returns only why it couldn't.</summary>
    [SupportedOSPlatform("linux")]
    static string Restart(List<string> folders, List<string> rest)
    {
        var abi = Landlock.Abi();
        if (abi < 1) return "this kernel has no Landlock.";

        var program = Environment.ProcessPath ?? "";
        List<string> argv = [program];
        // Started as "dotnet Bibliotaph.PdfWorker.dll", it starts again the same way.
        if (Path.GetFileNameWithoutExtension(program) == "dotnet" && typeof(Confinement).Assembly.Location is { Length: > 0 } dll) argv.Add(dll);
        argv.AddRange(rest);
        argv.Add(ConfinedArg);
        argv.Add(abi.ToString(CultureInfo.InvariantCulture));

        var rules = new Landlock.Rules(abi);
        foreach (var folder in SystemFolders().Concat(folders)) rules.Read(folder);
        rules.Execute(program);
        if (Interpreter(program) is { } interpreter) rules.Execute(interpreter);
        // The render buffer the host shares, opened again by the new worker.
        if (rest.IndexOf("--shared") is var shared and >= 0 && shared + 1 < rest.Count) rules.ReadWrite(rest[shared + 1]);
        foreach (var device in new[] { "/dev/null", "/dev/zero", "/dev/urandom", "/dev/random" }) rules.ReadWrite(device);
        // The runtime reads its own process's files, and the machine's memory and processors, but no other process's.
        rules.Read($"/proc/{Environment.ProcessId}");
        foreach (var file in new[] { "/proc/meminfo", "/proc/stat", "/proc/cpuinfo", "/proc/filesystems" }) rules.Read(file);

        if (rules.Restrict() is { } problem) return problem;
        var error = Landlock.Exec(program, argv);
        // Confined, but only this thread: better to stop than to read books half shut in.
        Console.Error.WriteLine($"Not confined: starting again inside failed ({error}).");
        Environment.Exit(3);
        return "";
    }

    /// <summary>Where the runtime and its libraries are, and what they read as they start: read-only, and nothing of anyone's.</summary>
    static IEnumerable<string> SystemFolders()
    {
        yield return AppContext.BaseDirectory;
        // dotnet/shared/Microsoft.NETCore.App/<version>/: the host and every framework are under dotnet/.
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        yield return Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        foreach (var folder in new[] { "/usr", "/lib", "/lib64", "/etc", "/sys" }) yield return folder;
    }

    /// <summary>The ELF interpreter named in <paramref name="program"/>'s PT_INTERP header, which execve opens to run it.</summary>
    static string? Interpreter(string program)
    {
        try
        {
            using var file = File.OpenRead(program);
            Span<byte> header = stackalloc byte[64];
            if (file.Read(header) < 64 || header[0] != 0x7F || header[1] != (byte)'E' || header[4] != 2) return null;
            var tableOffset = BitConverter.ToInt64(header[0x20..]);
            var entrySize = BitConverter.ToUInt16(header[0x36..]);
            var entries = BitConverter.ToUInt16(header[0x38..]);
            Span<byte> entry = stackalloc byte[56];
            for (var i = 0; i < entries; i++)
            {
                file.Position = tableOffset + (long)i * entrySize;
                if (file.Read(entry) < 56) return null;
                if (BitConverter.ToUInt32(entry) != 3) continue;
                var name = new byte[BitConverter.ToInt64(entry[0x20..])];
                file.Position = BitConverter.ToInt64(entry[0x08..]);
                file.ReadExactly(name);
                return Encoding.ASCII.GetString(name).TrimEnd('\0');
            }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>The Landlock system calls, as linux/landlock.h has them.</summary>
    [SupportedOSPlatform("linux")]
    static partial class Landlock
    {
        const long CreateRuleset = 444, AddRule = 445, RestrictSelf = 446;
        const uint CreateRulesetVersion = 1;
        const int RulePathBeneath = 1;
        const int SetNoNewPrivileges = 38;
        const int OpenPath = 0x200000, OpenCloseOnExec = 0x80000;

        const ulong Execute = 1 << 0, WriteFile = 1 << 1, ReadFile = 1 << 2, ReadDir = 1 << 3;
        const ulong Refer = 1 << 13, Truncate = 1 << 14, IoctlDevice = 1 << 15;

        [StructLayout(LayoutKind.Sequential)]
        struct RulesetAttr
        {
            public ulong HandledAccessFs;
            public ulong HandledAccessNet;
            public ulong Scoped;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct PathBeneathAttr
        {
            public ulong AllowedAccess;
            public int ParentFd;
        }

        public static int Abi() => (int)syscall(CreateRuleset, IntPtr.Zero, 0, CreateRulesetVersion);

        /// <summary>The rules being built: every kind of access this ABI knows is refused unless a rule allows it.</summary>
        public sealed class Rules
        {
            readonly int _abi;
            readonly ulong _handled;
            readonly List<(string Path, ulong Access, bool File)> _rules = [];

            public Rules(int abi)
            {
                _abi = abi;
                // ABI 1 knows the first 13 kinds; later ABIs add linking across folders, truncating and device ioctls.
                _handled = (1UL << 13) - 1;
                if (abi >= 2) _handled |= Refer;
                if (abi >= 3) _handled |= Truncate;
                if (abi >= 5) _handled |= IoctlDevice;
            }

            public void Read(string path) => Add(path, ReadFile | ReadDir);

            public void ReadWrite(string path) => Add(path, ReadFile | WriteFile);

            public void Execute(string path) => Add(path, Landlock.Execute | ReadFile);

            void Add(string path, ulong access)
            {
                if (Directory.Exists(path)) _rules.Add((path, access, false));
                // A file's rule may hold only what can be done to a file.
                else if (File.Exists(path)) _rules.Add((path, access & ~ReadDir, true));
            }

            /// <returns>Why the thread couldn't be confined, or null when it is.</returns>
            public unsafe string? Restrict()
            {
                var attr = new RulesetAttr { HandledAccessFs = _handled };
                // Bind and connect over TCP, refused outright: no rule allows either.
                if (_abi >= 4) attr.HandledAccessNet = 0b11;
                // Abstract Unix sockets and signals, refused beyond the worker itself.
                if (_abi >= 6) attr.Scoped = 0b11;
                var size = _abi >= 6 ? 24 : _abi >= 4 ? 16 : 8;
                var ruleset = (int)syscall(CreateRuleset, (IntPtr)(&attr), (nuint)size, 0);
                if (ruleset < 0) return $"Landlock refused the ruleset ({Marshal.GetLastPInvokeErrorMessage()}).";
                try
                {
                    foreach (var (path, access, _) in _rules)
                    {
                        var fd = open(path, OpenPath | OpenCloseOnExec);
                        if (fd < 0) continue;
                        try
                        {
                            var rule = new PathBeneathAttr { AllowedAccess = access & _handled, ParentFd = fd };
                            if (syscall(AddRule, ruleset, RulePathBeneath, (IntPtr)(&rule), 0) < 0)
                                return $"Landlock refused the rule for {path} ({Marshal.GetLastPInvokeErrorMessage()}).";
                        }
                        finally
                        {
                            close(fd);
                        }
                    }
                    if (prctl(SetNoNewPrivileges, 1, 0, 0, 0) < 0) return $"no_new_privs was refused ({Marshal.GetLastPInvokeErrorMessage()}).";
                    if (syscall(RestrictSelf, ruleset, 0) < 0) return $"Landlock refused to confine ({Marshal.GetLastPInvokeErrorMessage()}).";
                    return null;
                }
                finally
                {
                    close(ruleset);
                }
            }
        }

        /// <summary>Replaces this program with <paramref name="program"/>, in this process. Returns only on failure.</summary>
        public static string Exec(string program, IReadOnlyList<string> argv)
        {
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(e => $"{e.Key}={e.Value}").ToList();
            var pointers = new List<IntPtr>();
            try
            {
                var args = Terminated(argv, pointers);
                var env = Terminated(environment, pointers);
                execve(program, args, env);
                return Marshal.GetLastPInvokeErrorMessage();
            }
            finally
            {
                foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer);
            }
        }

        /// <summary>A null-terminated array of UTF-8 strings, as execve takes, freed through <paramref name="owned"/>.</summary>
        static IntPtr Terminated(IReadOnlyList<string> strings, List<IntPtr> owned)
        {
            var array = Marshal.AllocCoTaskMem(IntPtr.Size * (strings.Count + 1));
            owned.Add(array);
            for (var i = 0; i < strings.Count; i++)
            {
                var text = Marshal.StringToCoTaskMemUTF8(strings[i]);
                owned.Add(text);
                Marshal.WriteIntPtr(array, i * IntPtr.Size, text);
            }
            Marshal.WriteIntPtr(array, strings.Count * IntPtr.Size, IntPtr.Zero);
            return array;
        }

        [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static partial long syscall(long number, IntPtr attr, nuint size, uint flags);

        [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static partial long syscall(long number, int ruleset, int ruleType, IntPtr attr, uint flags);

        [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static partial long syscall(long number, int ruleset, uint flags);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int open(string path, int flags);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int close(int fd);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int execve(string path, IntPtr argv, IntPtr envp);
    }
}
