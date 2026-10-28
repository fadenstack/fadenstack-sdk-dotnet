namespace Fadenstack.Http;

/// <summary>
/// A request's route as it may be logged: the path without its query, with every segment that is not a plain lower-case
/// word (an id, a file name, anything with a digit) replaced by <c>{id}</c> -- <c>/v1/sessions/{id}/messages</c>.
/// </summary>
internal static class RouteTemplate
{
    internal static string Of(Uri uri)
    {
        var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => IsWord(s) ? s : "{id}");
        return "/" + string.Join("/", segments);
    }

    /// <summary>A name of the API (<c>agents</c>, <c>agent-tokens</c>, <c>v1</c>), or an agent's slug without digits.</summary>
    private static bool IsWord(string segment)
    {
        if (segment.Length is 0 or > 40)
        {
            return false;
        }
        if (segment[0] == 'v' && segment.Length > 1 && segment.Skip(1).All(char.IsDigit))
        {
            return true;
        }
        return segment.All(c => c is >= 'a' and <= 'z' || c is '-' or '_');
    }
}
