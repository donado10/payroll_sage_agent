namespace PayrollSageAgent.Core.Tests;

/// <summary>The repo root, found by its marker file — tests read <c>platform/</c> from there.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Find();

    public static string Platform(string file) => Path.Combine(Root, "platform", file);

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".payroll-agent-repo")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repo root not found: no .payroll-agent-repo above the test binaries.");
    }
}
