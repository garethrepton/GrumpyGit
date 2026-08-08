using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.Core.Agents;

/// <summary>
/// Builds the agent for the chosen module, and is the only place that maps a
/// <see cref="ReviewModuleId"/> to an implementation.
///
/// One function rather than a container: there are three modules, exactly one is live at a
/// time, and a registry with lifetimes and registration would be more machinery than the
/// thing it manages (commandment 3). Adding a fourth module is a case in the switch.
/// </summary>
public static class ReviewAgentFactory
{
    /// <summary>
    /// The agent for <paramref name="module"/>, or null when the feature is off.
    /// </summary>
    /// <param name="localModelPath">
    /// The GGUF for <see cref="ReviewModuleId.Local"/>. Ignored by the CLI modules, which
    /// have nothing on disk to point at.
    /// </param>
    /// <param name="agentWorkingDirectory">
    /// An empty directory the application owns, used as the working directory for CLI
    /// modules. Never a repository — see <see cref="AgentProcess"/>.
    /// </param>
    public static IReviewAgent? Create(
        ReviewModuleId module, string? localModelPath, string agentWorkingDirectory) => module switch
    {
        // The catalogue knows how a model it published wants its turns marked up, which
        // matters only for the ones whose file does not say — see ChatFormat.
        ReviewModuleId.Local => new LlamaLocalModel(
            localModelPath, ModelOption.ForPath(localModelPath)?.ChatFormat ?? ChatFormat.FromModel),

        ReviewModuleId.Copilot => new CopilotCliAgent(agentWorkingDirectory),
        ReviewModuleId.ClaudeCode => new ClaudeCodeAgent(agentWorkingDirectory),

        _ => null,
    };
}
