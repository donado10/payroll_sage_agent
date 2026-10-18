namespace PayrollSageAgent.Core.Update;

/// <summary>
/// The update folder — <c>%ProgramFiles%\IGF\PayrollSageAgent\update</c> on Windows: under Program Files, so
/// that only SYSTEM and the Administrators may WRITE there. [HARD] Not under the agent's data folder, which
/// the service's own account may modify: what SYSTEM installs must not be a file that account could plant.
/// <code>
/// update\
///   settings.json              how the agent updates itself (applied from agent.json)
///   state.json                 the last attempt, the last check
///   installed.msi              the MSI of the version installed now — init-config copies it — for a rollback
///   previous.msi               the same, set aside just before an install
///   PayrollSageAgent-X.Y.Z-x64.msi   a version downloaded and verified, until it is installed
///   runner-X.Y.Z\              a COPY of the exe, which the scheduled task runs: an install replaces the
///                              program's own exe, and a running exe cannot be replaced
///   runner.txt                 the path of the current runner — what Mettre-a-jour-agent.bat starts
///   *.log                      Windows Installer's logs of each install
/// </code>
/// </summary>
public sealed record UpdatePaths(string Folder)
{
    public const string FolderName = "update";
    public const string RunnerPrefix = "runner-";

    public string Settings => Path.Combine(Folder, "settings.json");
    public string State => Path.Combine(Folder, "state.json");
    public string Installed => Path.Combine(Folder, "installed.msi");
    public string Previous => Path.Combine(Folder, "previous.msi");
    public string Lock => Path.Combine(Folder, "update.lock");
    public string RunnerPointer => Path.Combine(Folder, "runner.txt");

    public string RunnerFolder(string version) => Path.Combine(Folder, RunnerPrefix + version);

    public string Download(string version) => Path.Combine(Folder, $"PayrollSageAgent-{version}-x64.msi");

    public string Log(string name) => Path.Combine(Folder, name + ".log");

    /// <summary>
    /// The program's own folder, seen from wherever this exe runs: the folder itself, or — from a runner copy,
    /// <c>…\PayrollSageAgent\update\runner-X.Y.Z\</c> — the folder two levels up. agent.json, the .bat files and
    /// the update folder are found from it.
    /// </summary>
    public static string InstallFolderOf(string baseDirectory)
    {
        var folder = Path.TrimEndingDirectorySeparator(baseDirectory);
        var parent = Path.GetDirectoryName(folder);
        return Path.GetFileName(folder).StartsWith(RunnerPrefix, StringComparison.OrdinalIgnoreCase)
            && parent is not null
            && string.Equals(Path.GetFileName(parent), FolderName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(parent) is { } install
            ? install
            : folder;
    }
}
