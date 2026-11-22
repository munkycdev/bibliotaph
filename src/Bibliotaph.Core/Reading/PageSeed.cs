namespace Bibliotaph.Core.Reading;

/// <summary>
/// A page drawn before its document was shown, at <see cref="Scale"/> pixels per point, so it shows the moment the
/// document does. <see cref="Preview"/> marks one drawn small to stand in until the page is drawn properly.
/// <see cref="Image"/> is the platform's own picture, which only the page surface that drew it looks inside.
/// </summary>
public sealed record PageSeed(int PageIndex, double Scale, object Image, bool Preview);
