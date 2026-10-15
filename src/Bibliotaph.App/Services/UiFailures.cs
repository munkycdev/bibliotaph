using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using Bibliotaph.Classification;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Viewer;

namespace Bibliotaph.App.Services;

/// <summary>
/// Which exceptions that reach the UI thread unhandled leave the app in a state worth carrying on from. These are
/// failures of something outside the app (a file, the clipboard, another program, the AI server, the PDF reader) that
/// stop one action before it changes anything of the app's own. Anything else is a bug that may have stopped an action
/// half way, with the screen and the databases out of step, so the app closes cleanly rather than carry on in that state.
/// </summary>
public static class UiFailures
{
    public static bool IsRecoverable(Exception ex) => Unwrap(ex) switch
    {
        AggregateException all => all.InnerExceptions.All(IsRecoverable),
        OperationCanceledException or IOException or UnauthorizedAccessException or ExternalException or HttpRequestException
            or ModelEndpointException or WorkerException or PdfOpenException or PdfRenderException => true,
        _ => false,
    };

    /// <summary>The exception itself, out of the reflection wrapper a command or binding can put round it.</summary>
    static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;
}
