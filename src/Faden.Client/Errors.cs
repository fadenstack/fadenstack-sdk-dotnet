using System.Net;

namespace Faden;

/// <summary>Something went wrong reaching Fadenstack, or the server refused the request.</summary>
public class FadenException : Exception
{
    public FadenException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>The server answered with an error: its status, and the OpenAI-shaped error's fields.</summary>
public class FadenApiException : FadenException
{
    public FadenApiException(HttpStatusCode status, string message, string? code = null, string? type = null,
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
}

/// <summary>401: no or a bad token. For an agent: sign in again.</summary>
public sealed class FadenAuthenticationException : FadenApiException
{
    public FadenAuthenticationException(HttpStatusCode status, string message, string? code = null)
        : base(status, message, code, "authentication_error")
    {
    }
}

/// <summary>403: the user may not do this (no grant, the agent is retired, ...).</summary>
public sealed class FadenPermissionException : FadenApiException
{
    public FadenPermissionException(HttpStatusCode status, string message, string? code = null, string? type = null)
        : base(status, message, code, type)
    {
    }
}

/// <summary>429: a rate limit or budget. <see cref="RetryAfter"/> says when to try again, if the server said.</summary>
public sealed class FadenRateLimitedException : FadenApiException
{
    public FadenRateLimitedException(HttpStatusCode status, string message, string? code, TimeSpan? retryAfter)
        : base(status, message, code, "rate_limit_error")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }

    /// <summary>A budget (EE), not a rate limit.</summary>
    public bool IsBudget => Code == "budget_exceeded";
}

/// <summary>The route is an Enterprise feature this server does not have (404 <c>ee_required</c>).</summary>
public sealed class FadenFeatureNotAvailableException : FadenApiException
{
    public FadenFeatureNotAvailableException(string message)
        : base(HttpStatusCode.NotFound, message, "ee_required")
    {
    }
}

/// <summary>An answer that had started broke off; the server said so before it ended.</summary>
public sealed class FadenStreamException : FadenException
{
    public FadenStreamException(string message, string? code) : base(message)
    {
        Code = code;
    }

    public string? Code { get; }
}

/// <summary>The agent's sign-in is gone (never made, expired, or revoked): sign in again.</summary>
public sealed class FadenSignInRequiredException : FadenException
{
    public FadenSignInRequiredException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
