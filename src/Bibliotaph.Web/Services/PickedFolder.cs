using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>A folder picked in storage and not yet added: where it is, and what's directly in it.</summary>
public sealed record PickedFolder(StorageAccount Storage, string Path, string Location, string Detail);
