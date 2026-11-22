using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Pdf.Host;

/// <summary>A document that would not open, with the worker's reason.</summary>
public sealed class PdfOpenException(ErrorKind kind, string? message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
}

/// <summary>A page the worker could not render, with its reason.</summary>
public sealed class PdfRenderException(ErrorKind kind, string? message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
}
