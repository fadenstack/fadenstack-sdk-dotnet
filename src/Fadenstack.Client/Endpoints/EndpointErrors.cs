using System.Net;

namespace Fadenstack.Endpoints;

/// <summary>The endpoint refused the key (401, or 403 with a key): a wrong, expired or revoked key.</summary>
public sealed class EndpointKeyRejectedException : FadenstackApiException
{
    public EndpointKeyRejectedException(HttpStatusCode status, string message, string? code = null)
        : base(status, message, code, "authentication_error")
    {
    }
}

/// <summary>The endpoint refused the request's origin (403): a local server that admits only some origins (Ollama's <c>OLLAMA_ORIGINS</c>).</summary>
public sealed class EndpointOriginRefusedException : FadenstackApiException
{
    public EndpointOriginRefusedException(HttpStatusCode status, string message, string? code = null)
        : base(status, message, code, "permission_error")
    {
    }
}

/// <summary>
/// What answered is not an OpenAI-compatible chat endpoint at that address: no such route (404 without a model error), or an
/// answer in another shape. Usually a wrong base (it includes <c>/v1</c>).
/// </summary>
public sealed class EndpointNotCompatibleException : FadenstackApiException
{
    public EndpointNotCompatibleException(HttpStatusCode status, string message, string? code = null)
        : base(status, message, code, "not_compatible")
    {
    }
}

/// <summary>The endpoint does not have the model asked for (Ollama: not pulled; a cloud: not deployed or not allowed).</summary>
public sealed class EndpointModelNotFoundException : FadenstackApiException
{
    public EndpointModelNotFoundException(HttpStatusCode status, string message, string? code = null)
        : base(status, message, code, "model_not_found")
    {
    }
}

/// <summary>The request was longer than the model's context: fewer turns, shorter instructions, or a model with a longer context.</summary>
public sealed class EndpointContextLengthException : FadenstackApiException
{
    public EndpointContextLengthException(HttpStatusCode status, string message, string? code = null)
        : base(status, message, code, "context_length_exceeded")
    {
    }
}

/// <summary>Nothing answered at the endpoint's address (refused, not found, or no answer in time).</summary>
public sealed class EndpointUnreachableException : FadenstackUnreachableException
{
    public EndpointUnreachableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Why a plain-http address is refused (D3; OWASP ASVS 5.0 V12).</summary>
public enum PlainHttpRefusal
{
    /// <summary>The address is outside this device and outside a private network.</summary>
    NotLocal,

    /// <summary>A private-network address, without the person's opt-in for this endpoint.</summary>
    NoOptIn,

    /// <summary>A key would travel unencrypted.</summary>
    KeyOverPlainHttp,

    /// <summary>The machine policy forbids plain http in the local network (F09).</summary>
    ForbiddenByPolicy,
}

/// <summary>The SDK would not send this request over plain http (nothing was sent).</summary>
public sealed class EndpointPlainHttpRefusedException : FadenstackException
{
    public EndpointPlainHttpRefusedException(PlainHttpRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    public PlainHttpRefusal Reason { get; }
}
