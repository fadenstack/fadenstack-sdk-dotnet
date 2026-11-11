using System.Net;
using System.Text.Json;

namespace Fadenstack;

/// <summary>Something went wrong reaching Fadenstack, or the server refused the request.</summary>
public class FadenstackException : Exception
{
    public FadenstackException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>The server answered with an error: its status, and the OpenAI-shaped error's fields.</summary>
public class FadenstackApiException : FadenstackException
{
    public FadenstackApiException(HttpStatusCode status, string message, string? code = null, string? type = null,
        string? param = null) : base(message)
    {
        Status = status;
        Code = code;
        Type = type;
        Param = param;
    }

    public HttpStatusCode Status { get; }

    /// <summary>The error code, e.g. <c>agent_not_granted</c>, <c>tool_not_in_contract</c>.</summary>
    public string? Code { get; }

    public string? Type { get; }

    public string? Param { get; }

    /// <summary>
    /// The error's <c>faden</c> block, as the server sent it (decision id, governance, ...), or null: kept for every error,
    /// whatever its status, so a refusal's details are not lost.
    /// </summary>
    public JsonElement? Faden { get; internal set; }
}

/// <summary>401: no or a bad token. For an agent: sign in again.</summary>
public sealed class FadenstackAuthenticationException : FadenstackApiException
{
    public FadenstackAuthenticationException(HttpStatusCode status, string message, string? code = null)
        : base(status, message, code, "authentication_error")
    {
    }
}

/// <summary>403: the user may not do this (no grant, the agent is retired, ...).</summary>
public sealed class FadenstackPermissionException : FadenstackApiException
{
    public FadenstackPermissionException(HttpStatusCode status, string message, string? code = null, string? type = null)
        : base(status, message, code, type)
    {
    }
}

/// <summary>429: a limit applied. <see cref="FadenstackApiException.Code"/> says which; <see cref="RetryAfter"/> says when to try again, if the server said.</summary>
public sealed class FadenstackRateLimitedException : FadenstackApiException
{
    public FadenstackRateLimitedException(HttpStatusCode status, string message, string? code, TimeSpan? retryAfter)
        : base(status, message, code, "rate_limit_error")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>An answer that had started broke off; the server said so before it ended.</summary>
public sealed class FadenstackStreamException : FadenstackException
{
    public FadenstackStreamException(string message, string? code) : base(message)
    {
        Code = code;
    }

    public string? Code { get; }
}

/// <summary>The agent's sign-in is gone (never made, expired, or revoked): sign in again.</summary>
public sealed class FadenstackSignInRequiredException : FadenstackException
{
    public FadenstackSignInRequiredException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
