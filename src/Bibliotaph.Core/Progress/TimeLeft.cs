namespace Bibliotaph.Core.Progress;

/// <summary>An estimate in words, rounded so it doesn't claim more precision than it has: "about 25 minutes".</summary>
public static class TimeLeft
{
    public static string Describe(TimeSpan left)
    {
        var minutes = left.TotalMinutes;
        if (minutes < 1) return "less than a minute";
        if (minutes < 1.5) return "about a minute";
        if (minutes < 10) return $"about {Math.Round(minutes):0} minutes";
        if (minutes < 57.5) return $"about {Math.Round(minutes / 5) * 5:0} minutes";
        if (minutes < 115)
        {
            var rest = Math.Round((minutes - 60) / 10) * 10;
            return rest <= 0 ? "about an hour" : $"about 1 hour {rest:0} minutes";
        }
        var hours = left.TotalHours;
        if (hours < 47.5) return $"about {Math.Round(hours):0} hours";
        return $"about {Math.Round(left.TotalDays):0} days";
    }
}
