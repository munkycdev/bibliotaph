using System.Diagnostics;
using Bibliotaph.Processing;

namespace Bibliotaph.Server;

public static class ForegroundRequests
{
    /// <summary>Marks an endpoint that a page asks again and again to watch the background work, which mustn't hold that work back.</summary>
    sealed class WatchesBackground;

    static readonly WatchesBackground Watching = new();

    /// <summary>
    /// An endpoint asked again and again to see how the background work is going, such as the sidebar's status: it is
    /// answered without counting as someone waiting, or watching the work would slow it down.
    /// </summary>
    public static TBuilder WatchesBackgroundWork<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(Watching);

    /// <summary>An answer slower than this is logged with its route, so a slow page can be traced to the call behind it.</summary>
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Marks each API call while it is answered, so every library's background reading waits for a quiet moment
    /// (<see cref="ForegroundActivity"/>), and logs the slow ones. The route is logged, not the path, which can hold ids.
    /// A connected PC's calls (its key in a Bearer header) and calls that only watch the work don't count: nobody is
    /// waiting on a page for them.
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
            var waiting = context.GetEndpoint()?.Metadata.GetMetadata<WatchesBackground>() is null
                && !context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            using (waiting ? foreground.Begin() : null) await next(context);
            var took = Stopwatch.GetElapsedTime(started);
            if (took >= Slow)
                log.LogInformation("{Method} {Route} took {Milliseconds:N0} ms", context.Request.Method,
                    (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)", took.TotalMilliseconds);
        });
    }
}
