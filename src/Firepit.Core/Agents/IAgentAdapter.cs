using Firepit.Core.Projects;

namespace Firepit.Core.Agents;

public interface IAgentAdapter
{
    string Id { get; }

    string DisplayName { get; }

    IReadOnlyList<string> ProjectMarkers { get; }

    AgentLaunchSpec BuildLaunchSpec(ProjectContext context, AgentLaunchOptions options);

    /// <summary>
    /// True when the agent left a conversation on disk for this project that
    /// a resume launch (<see cref="AgentLaunchOptions.Resume"/>) would pick
    /// up. Best-effort: adapters without a cheap way to tell keep the default
    /// and the host simply never offers a resume choice.
    /// </summary>
    bool HasResumableSession(ProjectContext context) => false;
}
