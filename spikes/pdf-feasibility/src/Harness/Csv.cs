using System.Globalization;
using System.Text;

namespace Bibliotaph.Spike.Harness;

/// <summary>Minimal CSV writer; values are quoted when they need to be.</summary>
sealed class Csv : IDisposable
{
    readonly StreamWriter _writer;

    public Csv(string path, params string[] header)
    {
        _writer = new StreamWriter(path, false, new UTF8Encoding(true));
        Row(header);
    }

    public void Row(params object?[] values)
    {
        _writer.WriteLine(string.Join(",", values.Select(Format)));
        _writer.Flush();
    }

    static string Format(object? value)
    {
        var s = value switch
        {
            null => "",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            float f => f.ToString("0.##", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public void Dispose() => _writer.Dispose();
}
