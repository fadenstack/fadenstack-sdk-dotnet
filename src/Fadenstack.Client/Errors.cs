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

    /// <summary>The error code, e.g. <c>agent_not_granted</c>, <c>bypass_step_up_required</c>.</summary>
    public string? Code { get; }

    public string? Type { get; }

    public string? Param { get; }

    /// <summary>
    /// The error's <c>faden</c> block, as the server sent it (decision id, governance, ...), or null: kept for every error,
    /// whatever its status, so a refusal's details are not lost.
    /// </summary>
    public JsonElement? Faden { get; internal set; }

    /// <summary>
    /// The server refused a picture because image generation is off in this version (core 0.7.1, EU AI Act Article 5): a 403 whose code is
    /// <see cref="ErrorCodes.ImagesDisabled"/>. Other 403s are not.
    /// </summary>
    public bool IsImagesDisabled => (int)Status == 403 && string.Equals(Code, ErrorCodes.ImagesDisabled, StringComparison.Ordinal);

    /// <summary>
    /// The organisation turned this agent off (core 0.7.1, the off-switch): a 403 whose code is <see cref="ErrorCodes.AgentDisabled"/>.
    /// It is not temporary and not worth a retry: the agent stays off until an administrator turns it on. Other 403s are not.
    /// TypeScript: <c>isAgentDisabled(error)</c>.
    /// </summary>
    public bool IsAgentDisabled => (int)Status == 403 && string.Equals(Code, ErrorCodes.AgentDisabled, StringComparison.Ordinal);
}

/// <summary>Error codes the server sends that an app may act on (<see cref="FadenstackApiException.Code"/>).</summary>
public static class ErrorCodes
{
    /// <summary>
    /// 403, error type <c>invalid_request_error</c>: image generation (<c>/v1/images/generations</c>, <see cref="Resources.AgentResource.GenerateImagesAsync"/>)
    /// is turned off in this version, and <see cref="Models.AgentProfile.Images"/> is false. It is not temporary: do not retry.
    /// </summary>
    public const string ImagesDisabled = "images_disabled";

    /// <summary>
    /// 403, error type <c>permission_error</c>: the organisation turned this agent off (core 0.7.1). Every agent route but the profile, the
    /// events and the manifest answers it until the agent is turned on again; do not retry (<see cref="FadenstackApiException.IsAgentDisabled"/>).
    /// TypeScript: <c>errorCodes.agentDisabled</c>.
    /// </summary>
    public const string AgentDisabled = "agent_disabled";
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

/// <summary>Nothing answered at the server's address: refused, not found, no answer in time (sdk/51 I15).</summary>
public class FadenstackUnreachableException : FadenstackException
{
    public FadenstackUnreachableException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    /// <summary>
    /// The typed error for a request that got no answer: <see cref="ServerCertificateException"/> when TLS refused the
    /// server's certificate, else unreachable with <paramref name="message"/>.
    /// </summary>
    internal static FadenstackException Of(Exception error, string message)
    {
        for (var inner = error; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Security.Authentication.AuthenticationException)
            {
                return new ServerCertificateException(
                    "The server's certificate is not trusted (an unknown CA, a name that does not match, or expired). Ask IT: a CA of your own comes from the machine policy or the app's settings.", error);
            }
        }
        return new FadenstackUnreachableException(message, error);
    }
}

/// <summary>
/// The server's certificate is not trusted (an unknown CA, a name that does not match, expired). A CA of one's own is
/// trusted through the connection's settings or the machine policy, never by a click (ASVS 5.0 V12).
/// </summary>
public sealed class ServerCertificateException : FadenstackException
{
    public ServerCertificateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// The server named a sign-in page on another address than its own, and the SDK did not use it. A server builds the page
/// from the request's <c>Host</c> and <c>X-Forwarded-Proto</c>: use the address the console uses (https, the public name).
/// </summary>
public sealed class FadenstackPairingPageElsewhereException : FadenstackException
{
    public FadenstackPairingPageElsewhereException(string message) : base(message)
    {
    }
}

/// <summary>An address that must be https is not (plain http only to this machine, or where a setting allows it).</summary>
public sealed class FadenstackHttpsRequiredException : ArgumentException
{
    public FadenstackHttpsRequiredException(string message, string? paramName = null) : base(message, paramName)
    {
    }
}

/// <summary>The e-mail or password is not right (the password sign-in of a server without pairing).</summary>
public sealed class FadenstackBadCredentialsException : FadenstackApiException
{
    public FadenstackBadCredentialsException(HttpStatusCode status, string message) : base(status, message, "LOGIN_BAD_CREDENTIALS")
    {
    }
}

/// <summary>The agent's sign-in is gone (never made, expired, or revoked): sign in again.</summary>
public sealed class FadenstackSignInRequiredException : FadenstackException
{
    public FadenstackSignInRequiredException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
