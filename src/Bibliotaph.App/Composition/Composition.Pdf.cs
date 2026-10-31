using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>PDFium runs only in worker processes; none starts until something asks for a page.</summary>
    public static IServiceCollection AddPdfWorkers(this IServiceCollection services)
    {
        services.AddSingleton(sp => new PdfWorkerPool(new PdfWorkerPoolOptions(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IPdfEngine, WorkerPdfEngine>();
        services.AddSingleton<ISourceFileReader, SourceFileReader>();
        return services;
    }
}
