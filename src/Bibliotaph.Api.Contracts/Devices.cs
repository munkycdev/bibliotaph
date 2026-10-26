namespace Bibliotaph.Api.Contracts;

/// <summary>A Windows PC asks to connect to a library. <see cref="Name"/> is how Settings on the web will list it.</summary>
public sealed record ConnectDevice(string Name);

/// <summary>
/// The PC's request is waiting: it opens <see cref="ApproveUrl"/> in the browser and shows <see cref="Code"/>, then asks
/// again with <see cref="PollToken"/>, which only it knows, until the user approves or declines.
/// </summary>
public sealed record DeviceConnectStarted(string Code, string PollToken, string ApproveUrl, DateTimeOffset Expires);

/// <summary>The PC asks again whether it was approved.</summary>
public sealed record DeviceConnectPoll(string PollToken);

/// <summary>
/// Where the PC's request is: waiting, connected, declined or expired. Once connected, <see cref="Key"/> is the PC's own
/// key, given this once, which it sends as a bearer token with every call.
/// </summary>
public sealed record DeviceConnectState(string State, string? Key = null, string? DeviceId = null)
{
    public const string Waiting = "waiting";
    public const string Connected = "connected";
    public const string Declined = "declined";
    public const string Expired = "expired";
}

/// <summary>A PC's request, as its approval page shows it to the signed-in user.</summary>
public sealed record DeviceConnectRequest(string Code, string Name, DateTimeOffset Expires);

/// <summary>A PC connected to the signed-in user's library, as Settings lists it.</summary>
public sealed record ConnectedDevice(string Id, string Name, DateTimeOffset Connected, DateTimeOffset? LastUsed);

/// <summary>What a connected PC learns about itself: whose library it's connected to.</summary>
public sealed record DeviceIdentity(string DeviceId, string Name, string UserName);
