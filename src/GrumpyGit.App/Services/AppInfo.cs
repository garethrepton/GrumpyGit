using System.Reflection;

namespace GrumpyGit.App.Services;

/// <summary>
/// The product name, taken from the assembly rather than written twice.
///
/// Two editions ship from this codebase — "Grumpy" and "Grumpy AI" — and they are
/// separate installs that can sit side by side. Which one a window belongs to has
/// to be visible on screen, and the answer must be the same one the exe's file
/// properties and the installer give, so it comes from &lt;Product&gt; in the
/// csproj and nowhere else.
/// </summary>
public static class AppInfo
{
    public static string ProductName { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Grumpy";
}
