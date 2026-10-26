using System.Reflection;

namespace Zexoro;

/// <summary>Identity of the zexoro build that is currently running.</summary>
public static class ProductInfo
{
    public const string Name = "zexoro";

    /// <summary>The informational version, e.g. <c>0.1.0-dev+3f2a9c1</c>.</summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";
}
