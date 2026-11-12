using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// Which still a video's cover is drawn from. Animated maps come with stills of the same scenes, named alike
/// ("Harbor_Night_Animated.webm" beside "Harbor_Night.jpg" and "Harbor_Day.jpg"), so the still whose name shares the
/// most words with the video's is the closest picture of it.
/// </summary>
public static class VideoStills
{
    /// <summary>Words that say a file moves, or how big it is, rather than what it shows.</summary>
    static readonly HashSet<string> Noise = new(StringComparer.Ordinal) { "animated", "animation", "anim", "loop", "looped", "video", "webm", "still", "static" };

    /// <summary>
    /// The still for the video at <paramref name="videoPath"/>: the one sharing the most words with its name, then the
    /// fewest words besides, then the first in natural order. Null when there are none.
    /// </summary>
    public static long? Pick(string videoPath, IReadOnlyList<StillBeside> stills)
    {
        if (stills.Count == 0) return null;
        var words = Words(videoPath);
        return stills
            .Select(s => (Still: s, Words: Words(s.Path)))
            .OrderByDescending(s => s.Words.Count(words.Contains))
            .ThenBy(s => s.Words.Count(w => !words.Contains(w)))
            .ThenBy(s => Path.GetFileName(s.Still.Path), NaturalOrder.Instance)
            .First().Still.DocumentId;
    }

    /// <summary>The words of a file's name, lowercased and split at anything but letters and digits, and between a lowercase letter and a capital.</summary>
    static HashSet<string> Words(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
        var words = new HashSet<string>(StringComparer.Ordinal);
        var word = new System.Text.StringBuilder();
        void End()
        {
            if (word.Length > 0 && !Noise.Contains(word.ToString())) words.Add(word.ToString());
            word.Clear();
        }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                End();
                continue;
            }
            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) End();
            word.Append(char.ToLowerInvariant(c));
        }
        End();
        return words;
    }
}
