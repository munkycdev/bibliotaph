using System.Diagnostics;
using Bibliotaph.Processing;

namespace Bibliotaph.Server;

public static class ForegroundRequests
{
    /// <summary>An answer slower than this is logged with its route, so a slow page can be traced to the call behind it.</summary>
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Marks each API call while it is answered, so every library's background reading waits for a quiet moment
    /// (<see cref="ForegroundActivity"/>), and logs the slow ones. The route is logged, not the path, which can hold ids.
    /// </summary>
    public static IApplicationBuilder UseForegroundRequests(this IApplicationBuilder app)
    {
        var foreground = app.ApplicationServices.GetRequiredService<ForegroundActivity>();
        var log = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ForegroundRequests));
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }
            var started = Stopwatch.GetTimestamp();
            using (foreground.Begin()) await next(context);
            var took = Stopwatch.GetElapsedTime(started);
            if (took >= Slow)
                log.LogInformation("{Method} {Route} took {Milliseconds:N0} ms", context.Request.Method,
                    (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)", took.TotalMilliseconds);
        });
    }
}
