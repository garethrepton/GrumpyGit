using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using GrumpyGit.Core.Git;

namespace GrumpyGit.App.Services;

public static class GitConfigService
{
    public static async Task<string> GetGlobalConfigAsync(string key)
    {
        var result = await GitProcess.Start()
            .WithArguments(args => args.Add("config").Add("--global").Add("--get").Add(key))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
    }

    public static async Task SetGlobalConfigAsync(string key, string value)
    {
        var result = await GitProcess.Start()
            .WithArguments(args => args.Add("config").Add("--global").Add(key).Add(value))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (result.ExitCode != 0)
            throw new System.Exception($"Failed to set git config {key}: {result.StandardError}");
    }
}
