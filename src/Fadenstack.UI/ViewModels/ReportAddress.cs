using Fadenstack.Agents;

namespace Fadenstack.UI.ViewModels;

/// <summary>Whom a report goes to, by how the agent is connected.</summary>
public static class ReportAddress
{
    /// <summary>
    /// The machine policy's <c>supportEmail</c> first (both modes). Standalone only, after it: the host's address
    /// (<see cref="AgentChatOptions.SupportEmail"/>), then <see cref="SupportAddress.Default"/>. Governed with no policy address: null, so no
    /// mail (the organisation's address only, never ours). An address that is not <see cref="SupportAddress.IsValid"/> counts as absent.
    /// </summary>
    public static string? Resolve(AgentConnection connection, MachinePolicy policy, string? hostAddress)
    {
        if (policy is null)
        {
            throw new ArgumentNullException(nameof(policy));
        }
        if (SupportAddress.IsValid(policy.SupportEmail))
        {
            return policy.SupportEmail;
        }
        if (connection != AgentConnection.Standalone)
        {
            return null;
        }
        return SupportAddress.IsValid(hostAddress) ? hostAddress : SupportAddress.Default;
    }
}
