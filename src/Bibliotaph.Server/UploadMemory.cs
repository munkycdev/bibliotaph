namespace Bibliotaph.Server;

/// <summary>
/// The memory every user's uploads may take at once, shared, since they share one small container: a sync file, a PC's
/// results, a library page and its pictures, a cover. Each is held in memory while it's read, and a library page's
/// pictures while its preview is looked at, so without a limit a few people uploading at once could take the server
/// down for everyone. An upload that doesn't fit is turned away, to be tried again shortly.
/// </summary>
public sealed class UploadMemory(TimeProvider clock, long budget)
{
    /// <summary>What a turned-away upload is told.</summary>
    public const string Busy = "Bibliotaph is busy with other people's uploads. Try again in a minute.";

    readonly Lock _gate = new();
    readonly List<Reservation> _held = [];

    public long Budget => budget;

    /// <summary>Memory taken now.</summary>
    public long Taken
    {
        get
        {
            lock (_gate) return Sweep();
        }
    }

    /// <summary>
    /// Takes <paramref name="bytes"/> until the reservation is disposed, or until <paramref name="until"/> if that comes
    /// first, for what's kept beyond the request; null when they don't fit.
    /// </summary>
    public Reservation? TryReserve(long bytes, DateTimeOffset? until = null)
    {
        lock (_gate)
        {
            if (Sweep() + bytes > budget) return null;
            var reservation = new Reservation(this, bytes, until);
            _held.Add(reservation);
            return reservation;
        }
    }

    /// <summary>
    /// Takes the memory a request's body needs, at most <paramref name="most"/> and <paramref name="copies"/> times over
    /// for an endpoint that holds it twice, until the response is sent; false when it doesn't fit.
    /// </summary>
    public static bool ReserveFor(HttpContext context, long most, int copies = 1)
    {
        if (context.RequestServices.GetService<UploadMemory>() is not { } memory) return true;
        if (memory.TryReserve(Math.Min(context.Request.ContentLength ?? most, most) * copies) is not { } reservation) return false;
        context.Response.RegisterForDispose(reservation);
        return true;
    }

    /// <summary>The 503 an upload that doesn't fit gets.</summary>
    public static IResult BusyResult(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "30";
        return Results.Problem(Busy, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    long Sweep()
    {
        var now = clock.GetUtcNow();
        _held.RemoveAll(r => r.Until <= now);
        return _held.Sum(r => r.Bytes);
    }

    void Release(Reservation reservation)
    {
        lock (_gate) _held.Remove(reservation);
    }

    public sealed class Reservation(UploadMemory owner, long bytes, DateTimeOffset? until) : IDisposable
    {
        public long Bytes { get; } = bytes;
        public DateTimeOffset? Until { get; } = until;

        public void Dispose() => owner.Release(this);
    }
}
