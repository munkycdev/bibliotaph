namespace Bibliotaph.Core;

/// <summary>Whether the queue starts again when it ends, or the track does.</summary>
public enum RepeatMode { Off, All, One }

/// <summary>
/// The order a player's queue plays in, by the tracks' places in the queue: as listed, or shuffled with the track
/// playing first, and what Next and Previous move to with repeat off, all or one. The Windows, Mac and web players
/// share it, so they step through an album alike.
/// </summary>
public sealed class PlayOrder(Random? random = null)
{
    readonly Random _random = random ?? Random.Shared;
    List<int> _order = [];
    int _at = -1;

    /// <summary>How long a track plays, or how far into it: "4:32", or "1:02:15" past an hour.</summary>
    public static string Length(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
        : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    /// <summary>How many tracks the queue has.</summary>
    public int Count => _order.Count;

    public RepeatMode Repeat { get; set; }

    public bool Shuffle { get; private set; }

    /// <summary>The place in the queue of the track playing or paused; null with an empty queue.</summary>
    public int? Current => _at >= 0 && _at < _order.Count ? _order[_at] : null;

    /// <summary>Next has a track to go to: a later one, or the first again with repeat all.</summary>
    public bool HasNext => Current is not null && (_at < _order.Count - 1 || Repeat == RepeatMode.All);

    /// <summary>A new queue of <paramref name="count"/> tracks, playing from <paramref name="start"/>.</summary>
    public void Start(int count, int start)
    {
        if (count <= 0)
        {
            Clear();
            return;
        }
        Reorder(count, Math.Clamp(start, 0, count - 1));
    }

    public void Clear()
    {
        _order = [];
        _at = -1;
    }

    /// <summary>Plays the track at <paramref name="index"/> in the queue, as picking it from the list does.</summary>
    public void Jump(int index)
    {
        var at = _order.IndexOf(index);
        if (at >= 0) _at = at;
    }

    /// <summary>Shuffle on puts the rest after the track playing in any order; off, the queue plays on as listed from it.</summary>
    public void SetShuffle(bool on)
    {
        Shuffle = on;
        if (Current is { } playing) Reorder(_order.Count, playing);
    }

    /// <summary>
    /// The track to play next: after one that <paramref name="ended"/> by itself with repeat one, the same again. Null at
    /// the end of the queue with repeat off, which goes back to its first track, to wait there.
    /// </summary>
    public int? Next(bool ended)
    {
        if (Current is null) return null;
        if (ended && Repeat == RepeatMode.One) return Current;
        if (_at < _order.Count - 1)
        {
            _at++;
            return Current;
        }
        if (Repeat != RepeatMode.All)
        {
            _at = 0;
            return null;
        }
        // Each time round a shuffled queue plays in a new order.
        if (Shuffle) Reorder(_order.Count, _random.Next(_order.Count));
        else _at = 0;
        return Current;
    }

    /// <summary>The track before, or the last with repeat all; null at the first, which starts again from its beginning.</summary>
    public int? Previous()
    {
        if (Current is null) return null;
        if (_at > 0)
        {
            _at--;
            return Current;
        }
        if (Repeat != RepeatMode.All) return null;
        _at = _order.Count - 1;
        return Current;
    }

    // As listed, at the track playing, so Previous goes to the one listed before it; shuffled, with it first.
    void Reorder(int count, int start)
    {
        if (!Shuffle)
        {
            _order = [.. Enumerable.Range(0, count)];
            _at = start;
            return;
        }
        var rest = Enumerable.Range(0, count).Where(i => i != start).ToArray();
        _random.Shuffle(rest);
        _order = [start, .. rest];
        _at = 0;
    }
}
