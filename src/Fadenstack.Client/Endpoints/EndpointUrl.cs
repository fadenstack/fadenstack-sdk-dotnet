namespace Fadenstack.Endpoints;

/// <summary>
/// The routes of an OpenAI-compatible endpoint, relative to its base (which includes <c>/v1</c>, the OpenAI convention):
/// <c>{base}/chat/completions</c> and <c>{base}/models</c>, with or without a trailing slash on the base. Azure OpenAI's v1
/// API is the same shape under <c>https://{resource}.openai.azure.com/openai/v1</c>. Never
/// <c>FadenstackTransport.Gateway()</c>, which puts <c>v1/...</c> under the server and would double it here.
/// </summary>
internal static class EndpointUrl
{
    /// <summary>The base with exactly one trailing slash; its query and fragment, if any, refused.</summary>
    public static Uri Base(Uri baseUrl)
    {
        if (baseUrl is null)
        {
            throw new ArgumentNullException(nameof(baseUrl));
        }
        if (!baseUrl.IsAbsoluteUri || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("An endpoint's address is an absolute http or https URL.", nameof(baseUrl));
        }
        if (baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
        {
            throw new ArgumentException("An endpoint's address has no query or fragment: give its base, such as https://host/v1.", nameof(baseUrl));
        }
        if (baseUrl.UserInfo.Length > 0)
        {
            // A key in the address would be written wherever the address is (settings, logs): it goes into the key store.
            throw new ArgumentException("An endpoint's address carries no user name or password: give the key separately.", nameof(baseUrl));
        }
        var path = baseUrl.AbsolutePath.TrimEnd('/') + "/";
        return new UriBuilder(baseUrl.Scheme, baseUrl.Host, baseUrl.Port, path).Uri;
    }

    public static Uri ChatCompletions(Uri baseUrl) => Route(baseUrl, "chat/completions");

    public static Uri Models(Uri baseUrl) => Route(baseUrl, "models");

    /// <summary>A route under the base: <c>Route(base, "models")</c>.</summary>
    public static Uri Route(Uri baseUrl, string relative) => new(Base(baseUrl), relative.TrimStart('/'));

    /// <summary>
    /// A route at the server's root rather than under the OpenAI base: Ollama's <c>/api/show</c>, vLLM's <c>/version</c>. The
    /// base's last <c>/v1</c> (and anything after it) is left out; a base without one keeps its path.
    /// </summary>
    public static Uri Root(Uri baseUrl, string relative)
    {
        var b = Base(baseUrl);
        var path = b.AbsolutePath;
        var at = path.LastIndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
        var root = at >= 0 ? path.Substring(0, at + 1) : path;
        return new Uri(new UriBuilder(b.Scheme, b.Host, b.Port, root).Uri, relative.TrimStart('/'));
    }

    /// <summary>
    /// Where a <c>Location</c> header points, resolved against the request's URI, the same on every OS: on Linux and macOS
    /// <c>new Uri("/v1/models", UriKind.RelativeOrAbsolute)</c> is an absolute <c>file:///v1/models</c>, so the header's own
    /// text is resolved instead (a leading <c>/</c> is a path, <c>//host/x</c> is another host). Null when it cannot be.
    /// </summary>
    public static Uri? ResolveLocation(Uri request, Uri? location) =>
        location is not null && Uri.TryCreate(request, location.OriginalString, out var target) ? target : null;

    /// <summary>The origin (scheme, host, port) a key may go to: requests and redirects never leave it.</summary>
    public static string Origin(Uri url) => url.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
}
