using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace Bibliotaph.PdfWorker;

/// <summary>
/// A PDF opened through FPDF_LoadCustomDocument over a read-only .NET stream. PDFium never sees the path,
/// so names Windows' ANSI code page can't hold (curly apostrophes, accents, CJK) open like any other, and
/// the file is never opened for writing.
/// </summary>
/// <remarks>
/// PDFiumCore's FPDF_FILEACCESS declares C <c>unsigned long</c> as 64 bits, but it is 32 bits on Windows.
/// The struct is the same size either way (the 32-bit length is padded), so this writes its own layout,
/// with <see cref="CULong"/> for the length and the callback's arguments, into the native block that
/// PDFiumCore's wrapper allocates. PDFium keeps that pointer for the life of the document, so the
/// wrapper lives until <see cref="Dispose"/>.
/// </remarks>
sealed unsafe class SourceDocument : IDisposable
{
    readonly FileStream _stream;
    readonly GCHandle _self;
    readonly FPDF_FILEACCESS _access = new();
    bool _disposed;

    SourceDocument(FileStream stream)
    {
        _stream = stream;
        _self = GCHandle.Alloc(this);
        *(FileAccessBlock*)_access.__Instance = new FileAccessBlock
        {
            FileLength = new CULong((nuint)stream.Length),
            GetBlock = &GetBlock,
            Param = GCHandle.ToIntPtr(_self),
        };
    }

    public FpdfDocumentT Handle { get; private set; } = null!;

    /// <summary>Opens the file, or returns null with PDFium's last error to report (or 2, file error, if .NET could not open it).</summary>
    public static SourceDocument? Open(string path, string? password, out uint error)
    {
        FileStream stream;
        try
        {
            // Share everything: Bibliotaph must never stop the user, a sync client or another app changing the file.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = 2; // FPDF_ERR_FILE
            return null;
        }

        var document = new SourceDocument(stream);
        var handle = fpdfview.FPDF_LoadCustomDocument(document._access, password);
        if (handle is null || handle.__Instance == IntPtr.Zero)
        {
            error = (uint)fpdfview.FPDF_GetLastError();
            document.Dispose();
            return null;
        }
        document.Handle = handle;
        error = 0;
        return document;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle is not null) fpdfview.FPDF_CloseDocument(Handle);
        _access.Dispose();
        _self.Free();
        _stream.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int GetBlock(IntPtr param, CULong position, byte* buffer, CULong size)
    {
        try
        {
            var self = (SourceDocument)GCHandle.FromIntPtr(param).Target!;
            var length = checked((int)size.Value);
            self._stream.Position = checked((long)position.Value);
            self._stream.ReadExactly(new Span<byte>(buffer, length));
            return 1;
        }
        catch (Exception)
        {
            // A file truncated or changed under us, or a cloud file that failed to download: PDFium reports it.
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileAccessBlock
    {
        public CULong FileLength;
        public delegate* unmanaged[Cdecl]<IntPtr, CULong, byte*, CULong, int> GetBlock;
        public IntPtr Param;
    }
}
