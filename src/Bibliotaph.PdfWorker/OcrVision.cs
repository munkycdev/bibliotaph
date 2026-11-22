#if !WINDOWS
using System.Runtime.InteropServices;

namespace Bibliotaph.PdfWorker;

/// <summary>
/// Apple's Vision text recognition, for the Mac app's worker: it ships with macOS, so nothing is installed for OCR, and
/// it reads the languages macOS knows. The worker is plain .NET, so Vision is called through the Objective-C runtime.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
sealed partial class VisionOcrEngine : IOcrEngine
{
    const string Objc = "/usr/lib/libobjc.A.dylib";
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string VisionFramework = "/System/Library/Frameworks/Vision.framework/Vision";

    // VNRequestTextRecognitionLevelAccurate: slower, but it reads small print and odd fonts as a book's pages need.
    const nint Accurate = 0;
    // kCGImageAlphaNoneSkipFirst | kCGBitmapByteOrder32Little: PDFium's BGRA with the alpha byte ignored.
    const uint BgraIgnoringAlpha = 6 | (2 << 12);

    VisionOcrEngine()
    {
    }

    public static VisionOcrEngine? TryCreate(out string? reason)
    {
        try
        {
            NativeLibrary.Load(VisionFramework);
        }
        catch (DllNotFoundException)
        {
            reason = "This Mac has no Vision framework to read scanned pages with.";
            return null;
        }
        if (objc_getClass("VNRecognizeTextRequest") == IntPtr.Zero)
        {
            reason = "This Mac's Vision framework can't recognise text.";
            return null;
        }
        reason = null;
        return new VisionOcrEngine();
    }

    public string Name => "Apple Vision";

    /// <summary>Vision has no limit of its own; this keeps a poster-sized page from taking the worker's memory.</summary>
    public int MaxImageDimension => 10000;

    public (string Text, List<(string Word, double X, double Y, double Width, double Height)> Words) Recognize(IntPtr bgra, int width, int height, int stride)
    {
        var words = new List<(string, double, double, double, double)>();
        var lines = new List<string>();
        var pool = objc_autoreleasePoolPush();
        var space = CGColorSpaceCreateDeviceRGB();
        var provider = CGDataProviderCreateWithData(IntPtr.Zero, bgra, (nuint)(stride * height), IntPtr.Zero);
        var image = CGImageCreate((nuint)width, (nuint)height, 8, 32, (nuint)stride, space, BgraIgnoringAlpha, provider, IntPtr.Zero, false, 0);
        var request = Send(Send(Class("VNRecognizeTextRequest"), "alloc"), "init");
        var handler = Send(Send(Class("VNImageRequestHandler"), "alloc"), "initWithCGImage:options:", image, Send(Class("NSDictionary"), "dictionary"));
        try
        {
            SendNint(request, "setRecognitionLevel:", Accurate);
            SendBool(request, "setUsesLanguageCorrection:", true);
            if (RespondsTo(request, "setAutomaticallyDetectsLanguage:")) SendBool(request, "setAutomaticallyDetectsLanguage:", true);
            var requests = Send(Class("NSArray"), "arrayWithObject:", request);
            if (!SendPerform(handler, Selector("performRequests:error:"), requests, IntPtr.Zero)) return (string.Empty, words);

            var results = Send(request, "results");
            var count = results == IntPtr.Zero ? 0 : SendCount(results, Selector("count"));
            for (nuint i = 0; i < count; i++)
            {
                var observation = SendIndex(results, Selector("objectAtIndex:"), i);
                var candidates = SendIndex(observation, Selector("topCandidates:"), 1);
                if (candidates == IntPtr.Zero || SendCount(candidates, Selector("count")) == 0) continue;
                var candidate = SendIndex(candidates, Selector("objectAtIndex:"), 0);
                var line = Marshal.PtrToStringUTF8(Send(Send(candidate, "string"), "UTF8String")) ?? "";
                if (line.Length == 0) continue;
                lines.Add(line);
                var lineBox = SendRect(observation, Selector("boundingBox"));
                foreach (var (start, length) in Words(line))
                {
                    var box = SendRange(candidate, Selector("boundingBoxForRange:error:"), new NSRange((nuint)start, (nuint)length), IntPtr.Zero) is var word && word != IntPtr.Zero
                        ? SendRect(word, Selector("boundingBox"))
                        : Share(lineBox, line.Length, start, length);
                    // Vision's boxes are fractions of the image from its bottom left; the worker's are pixels from the top left.
                    words.Add((line.Substring(start, length), box.X * width, (1 - box.Y - box.Height) * height, box.Width * width, box.Height * height));
                }
            }
            return (string.Join('\n', lines), words);
        }
        finally
        {
            Send(handler, "release");
            Send(request, "release");
            CGImageRelease(image);
            CGDataProviderRelease(provider);
            CGColorSpaceRelease(space);
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Where each word of a line starts and how long it is, in UTF-16 units as NSString counts them.</summary>
    static IEnumerable<(int Start, int Length)> Words(string line)
    {
        var start = -1;
        for (var i = 0; i <= line.Length; i++)
        {
            var space = i == line.Length || char.IsWhiteSpace(line[i]);
            if (!space && start < 0) start = i;
            else if (space && start >= 0)
            {
                yield return (start, i - start);
                start = -1;
            }
        }
    }

    /// <summary>A word's share of its line's box, by its place in the line, when Vision gives no box of its own.</summary>
    static CGRect Share(CGRect line, int lineLength, int start, int length) =>
        new(line.X + line.Width * start / lineLength, line.Y, line.Width * length / lineLength, line.Height);

    static IntPtr Class(string name) => objc_getClass(name);

    static IntPtr Selector(string name) => sel_registerName(name);

    static IntPtr Send(IntPtr target, string selector) => objc_msgSend(target, Selector(selector));

    static IntPtr Send(IntPtr target, string selector, IntPtr argument) => objc_msgSend(target, Selector(selector), argument);

    static IntPtr Send(IntPtr target, string selector, IntPtr first, IntPtr second) => objc_msgSend(target, Selector(selector), first, second);

    static void SendNint(IntPtr target, string selector, nint value) => objc_msgSend_nint(target, Selector(selector), value);

    static void SendBool(IntPtr target, string selector, bool value) => objc_msgSend_bool(target, Selector(selector), value);

    static bool RespondsTo(IntPtr target, string selector) => objc_msgSend_respondsTo(target, Selector("respondsToSelector:"), Selector(selector));

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct CGRect(double X, double Y, double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct NSRange(nuint Location, nuint Length);

    [LibraryImport(Objc, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(Objc, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(Objc)]
    private static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(Objc)]
    private static partial void objc_autoreleasePoolPop(IntPtr pool);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr target, IntPtr selector);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr target, IntPtr selector, IntPtr argument);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr target, IntPtr selector, IntPtr first, IntPtr second);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_nint(IntPtr target, IntPtr selector, nint value);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_bool(IntPtr target, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool objc_msgSend_respondsTo(IntPtr target, IntPtr selector, IntPtr query);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SendPerform(IntPtr target, IntPtr selector, IntPtr requests, IntPtr error);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial nuint SendCount(IntPtr target, IntPtr selector);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendIndex(IntPtr target, IntPtr selector, nuint index);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial CGRect SendRect(IntPtr target, IntPtr selector);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendRange(IntPtr target, IntPtr selector, NSRange range, IntPtr error);

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphics)]
    private static partial void CGColorSpaceRelease(IntPtr space);

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGDataProviderCreateWithData(IntPtr info, IntPtr data, nuint size, IntPtr release);

    [LibraryImport(CoreGraphics)]
    private static partial void CGDataProviderRelease(IntPtr provider);

    [LibraryImport(CoreGraphics)]
    private static partial IntPtr CGImageCreate(nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow,
        IntPtr space, uint bitmapInfo, IntPtr provider, IntPtr decode, [MarshalAs(UnmanagedType.U1)] bool shouldInterpolate, int intent);

    [LibraryImport(CoreGraphics)]
    private static partial void CGImageRelease(IntPtr image);
}
#endif
