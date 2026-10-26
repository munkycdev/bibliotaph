using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Accounts;

/// <summary>Where a request to connect a PC is: waiting for the user, connected (with its key, handed over once), or gone.</summary>
public enum ConnectState
{
    Waiting,
    Connected,
    Declined,
    Expired,
}

/// <summary>A PC's request to connect, as its approval page shows it.</summary>
public sealed record ConnectRequest(string Code, string Name, DateTimeOffset Expires);

/// <summary>What the PC learns when it asks again: the state, and once connected, its key and device ID.</summary>
public sealed record ConnectPoll(ConnectState State, string? Key = null, string? DeviceId = null);

/// <summary>
/// Windows PCs connected to a user's library, as a device signs in to a TV: the PC asks to connect and opens the
/// approval page in the browser, where the user, signed in, approves it; the PC then picks up a key of its own, which
/// it sends with every call. Only the key's SHA-256 is kept, and disconnecting the PC in Settings deletes it. Requests
/// waiting for approval are kept in memory: there is one server, and a lost one only means asking again.
/// </summary>
public sealed class DeviceService(AccountsDatabase database, TimeProvider clock)
{
    /// <summary>How long a request waits for the user to approve it.</summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most requests waiting at once, so asking anonymously can't fill the server's memory.</summary>
    const int MaxWaiting = 1000;

    /// <summary>How often a device's last use is written, so a busy PC doesn't write on every call.</summary>
    static readonly TimeSpan LastUsedEvery = TimeSpan.FromMinutes(5);

    public const string KeyPrefix = "btd_";

    /// <summary>The longest name a PC can give; a longer one is cut.</summary>
    const int MaxName = 64;

    sealed class Waiting(string code, string name, DateTimeOffset expires)
    {
        public string Code { get; } = code;
        public string Name { get; } = name;
        public DateTimeOffset Expires { get; } = expires;
        public ConnectPoll? Outcome { get; set; }
    }

    // By the PC's secret, which only it knows; the code in the approval link finds the same request.
    readonly ConcurrentDictionary<string, Waiting> _byPoll = new();
    readonly ConcurrentDictionary<string, string> _pollByCode = new();
    readonly ConcurrentDictionary<string, DateTimeOffset> _used = new();

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A PC asks to connect: returns the code for its approval link and the secret it asks again with, or null when
    /// too many requests are waiting.
    /// </summary>
    public (string Code, string PollToken, DateTimeOffset Expires)? Start(string name)
    {
        Sweep();
        if (_byPoll.Count >= MaxWaiting) return null;
        var tidy = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (tidy.Length == 0) tidy = "Windows PC";
        if (tidy.Length > MaxName) tidy = tidy[..MaxName];
        var code = ReadableCode();
        var poll = Secret(32);
        var expires = clock.GetUtcNow() + RequestLifetime;
        _byPoll[poll] = new Waiting(code, tidy, expires);
        _pollByCode[Normal(code)] = poll;
        return (code, poll, expires);
    }

    /// <summary>The request a code names, while it waits for approval. The code can be typed in any case, with or without its dash.</summary>
    public ConnectRequest? Find(string code) =>
        Open(code) is { } waiting ? new ConnectRequest(waiting.Code, waiting.Name, waiting.Expires) : null;

    /// <summary>The signed-in user approves the request: the PC is connected to their library, and its key waits to be picked up.</summary>
    public async Task<bool> ApproveAsync(string code, string userId, CancellationToken ct = default)
    {
        if (Open(code) is not { } waiting) return false;
        var key = KeyPrefix + Secret(32);
        var device = new Device { Id = Guid.NewGuid().ToString("N"), UserId = userId, Name = waiting.Name, KeyHash = Hash(key), Created = Now };
        await using var db = database.CreateContext();
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        waiting.Outcome = new ConnectPoll(ConnectState.Connected, key, device.Id);
        return true;
    }

    public bool Decline(string code)
    {
        if (Open(code) is not { } waiting) return false;
        waiting.Outcome = new ConnectPoll(ConnectState.Declined);
        return true;
    }

    /// <summary>The PC asks again. Its key is given once; after that, and once the request has expired, it is gone.</summary>
    public ConnectPoll Poll(string pollToken)
    {
        if (!_byPoll.TryGetValue(pollToken, out var waiting)) return new ConnectPoll(ConnectState.Expired);
        if (waiting.Outcome is { } outcome)
        {
            Forget(pollToken, waiting);
            return outcome;
        }
        if (waiting.Expires > clock.GetUtcNow()) return new ConnectPoll(ConnectState.Waiting);
        Forget(pollToken, waiting);
        return new ConnectPoll(ConnectState.Expired);
    }

    /// <summary>The device a key belongs to, or null for a key that isn't one or was disconnected.</summary>
    public async Task<Device?> AuthenticateAsync(string key, CancellationToken ct = default)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(key);
        await using var db = database.CreateContext();
        if (await db.Devices.FirstOrDefaultAsync(d => d.KeyHash == hash, ct) is not { } device) return null;
        var now = clock.GetUtcNow();
        if (!_used.TryGetValue(device.Id, out var last) || now - last >= LastUsedEvery)
        {
            _used[device.Id] = now;
            device.LastUsed = now.UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return device;
    }

    /// <summary>The user's connected PCs, the most recently connected first.</summary>
    public async Task<IReadOnlyList<Device>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.Devices.AsNoTracking().Where(d => d.UserId == userId).OrderByDescending(d => d.Created).ToListAsync(ct);
    }

    public async Task<Device?> FindAsync(string userId, string deviceId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct);
    }

    /// <summary>Disconnects one of the user's PCs: its key stops working. False when the user has no such PC.</summary>
    public async Task<bool> RemoveAsync(string userId, string deviceId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        if (await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct) is not { } device) return false;
        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        _used.TryRemove(deviceId, out _);
        return true;
    }

    Waiting? Open(string code) =>
        _pollByCode.TryGetValue(Normal(code), out var poll) && _byPoll.TryGetValue(poll, out var waiting)
        && waiting.Outcome is null && waiting.Expires > clock.GetUtcNow() ? waiting : null;

    void Forget(string poll, Waiting waiting)
    {
        _byPoll.TryRemove(poll, out _);
        _pollByCode.TryRemove(Normal(waiting.Code), out _);
    }

    /// <summary>Drops requests that have expired, approved or not, with nobody left to pick them up.</summary>
    void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (poll, waiting) in _byPoll)
            if (waiting.Expires + RequestLifetime < now) Forget(poll, waiting);
    }

    // No 0/O or 1/I, so the code the PC shows reads back the same on the approval page.
    const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Eight characters, such as K7XQ-M2PD, that the PC and its approval page both show, so the user can see they're
    /// approving the PC in front of them. 40 bits, alive for minutes, and only useful to someone signed in.
    /// </summary>
    static string ReadableCode()
    {
        Span<char> code = stackalloc char[9];
        for (var i = 0; i < code.Length; i++) code[i] = i == 4 ? '-' : CodeLetters[RandomNumberGenerator.GetInt32(CodeLetters.Length)];
        return new string(code);
    }

    static string Normal(string code) => code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    static string Secret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
