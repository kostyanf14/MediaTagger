namespace MediaTagger.API.Services;

/// <summary>
/// What this API offers the UI. An older server omits the list; the UI then treats
/// every name here as missing instead of calling a route that answers 404.
/// The client does not answer: the server never changes behaviour based on what the UI uses.
/// </summary>
public static class ApiFeatures
{
    public const string Recaps = "recaps";
    public const string UnnamedFaces = "unnamed-faces";

    public static readonly string[] All = { Recaps, UnnamedFaces };
}
