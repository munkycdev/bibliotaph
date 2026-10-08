using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.PdfWorker;
using PDFiumCore;

// The worker owns PDFium. It is started by WorkerClient, connects back over a named pipe,
// and handles one request at a time. Bitmaps are rendered straight into a shared memory-mapped
// file so large pages never go through the pipe.

// Used by the child-process isolation test: a harmless process to try to start.
if (args is ["--exit"]) return 0;

string? pipeName = null, sharedPath = null;
long sharedSize = 0;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--pipe": pipeName = args[++i]; break;
        case "--shared": sharedPath = args[++i]; break;
        case "--shared-size": sharedSize = long.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
    }
}
if (pipeName is null || sharedPath is null || sharedSize <= 0)
{
    Console.Error.WriteLine("Usage: Bibliotaph.PdfWorker --pipe <name> --shared <file> --shared-size <bytes>");
    return 2;
}

using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
pipe.Connect(15_000);

// The host holds the file open with FileShare.ReadWrite; we must allow the same or Windows refuses the open.
using var sharedFile = new FileStream(sharedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1);
using var mmf = MemoryMappedFile.CreateFromFile(sharedFile, null, sharedSize, MemoryMappedFileAccess.ReadWrite,
    HandleInheritability.None, leaveOpen: true);
using var view = mmf.CreateViewAccessor(0, sharedSize);

fpdfview.FPDF_InitLibrary();
unsafe
{
    byte* basePtr = null;
    view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
    try
    {
        var engine = new PdfEngine((IntPtr)basePtr, sharedSize);
        using var self = Process.GetCurrentProcess();
        while (true)
        {
            var request = Framing.Read<Request>(pipe);
            if (request is null) break;

            var sw = Stopwatch.StartNew();
            Response response;
            try
            {
                response = engine.Handle(request);
            }
            catch (OutOfMemoryException)
            {
                // The job object's memory cap was hit. PDFium's state can't be trusted after a failed
                // allocation, so die and let the host start a fresh worker.
                Environment.FailFast("Bibliotaph PdfWorker: out of memory under the job object cap.");
                throw;
            }
            catch (Exception ex)
            {
                response = Response.Fail(request.Id, ErrorKind.Internal, ex.GetType().Name + ": " + ex.Message);
            }
            self.Refresh();
            response = response with
            {
                Id = request.Id,
                WorkerMs = sw.Elapsed.TotalMilliseconds,
                WorkerWorkingSetBytes = self.WorkingSet64,
            };
            Framing.Write(pipe, response);
        }
        engine.CloseAll();
    }
    finally
    {
        view.SafeMemoryMappedViewHandle.ReleasePointer();
    }
}
fpdfview.FPDF_DestroyLibrary();
return 0;
