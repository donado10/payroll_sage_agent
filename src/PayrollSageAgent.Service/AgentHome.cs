using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PayrollSageAgent.Core;

namespace PayrollSageAgent.Service;

/// <summary>
/// Where the agent keeps its bindings and its logs on this machine.
///   Windows   %ProgramData%\IGF\PayrollSageAgent      (bindings\, logs\, pending\)
///   elsewhere $PAYROLL_AGENT_HOME, else ~/.payroll-sage-agent   (development only)
/// <c>PAYROLL_AGENT_HOME</c> overrides both — the Docker runs use <c>/repo/.agent-home</c>, gitignored.
///
/// [HARD] On Windows the folder is what keeps the secrets: DPAPI in MACHINE scope lets any process of
/// this machine unprotect them, so only SYSTEM, the Administrators and the service's own account may
/// open it. <c>install.ps1</c> sets those rights; <see cref="Ensure"/> sets the same ones when IT creates
/// the folder — an <c>add-binding</c> run before the installer would otherwise write under
/// %ProgramData%'s defaults, which let every local user READ.
/// </summary>
public sealed class AgentHome(string root)
{
    public string Root { get; } = root;
    public string Bindings => Path.Combine(Root, "bindings");
    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Requests the service carries out (<c>PendingRequests</c>) — the drop folder for a JSON config.</summary>
    public string Pending => Path.Combine(Root, "pending");

    /// <summary>
    /// <c>agent.json</c>, the agent's config: in the AGENT'S OWN FOLDER on Windows
    /// (<c>%ProgramFiles%\IGF\PayrollSageAgent</c>, beside the exe and the .bat files — the user's ask: « go to
    /// the agent, do the edit »); in the home under <c>PAYROLL_AGENT_HOME</c> (development).
    /// </summary>
    public string ConfigFile => Path.Combine(ConfigFolder, Core.Bindings.AgentConfigFile.FileName);

    /// <summary><c>agent.result.txt</c>, beside it: what the last <c>apply-config</c> did.</summary>
    public string ConfigReport => Path.Combine(ConfigFolder, Core.Bindings.AgentConfigFile.ReportName);

    /// <summary>
    /// The update folder (<see cref="Core.Update.UpdatePaths"/>): in the PROGRAM's folder on Windows — where only
    /// SYSTEM and the Administrators write, never the service's account —, in the home in development.
    /// </summary>
    public Core.Update.UpdatePaths Update => new(Path.Combine(ConfigFolder, Core.Update.UpdatePaths.FolderName));

    /// <summary><c>health.json</c>: what the service says of itself, read by the updater after an install.</summary>
    public string HealthFile => Path.Combine(Root, "health.json");

    /// <summary>
    /// The program's own folder on Windows — from a runner copy (<c>update\runner-X.Y.Z\</c>) too, which is
    /// where the scheduled update runs from (<see cref="Core.Update.UpdatePaths.InstallFolderOf"/>).
    /// </summary>
    public static string ProgramFolder => Core.Update.UpdatePaths.InstallFolderOf(AppContext.BaseDirectory);

    private string ConfigFolder =>
        OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYROLL_AGENT_HOME"))
            ? ProgramFolder
            : Root;

    /// <summary>
    /// [HARD] <c>agent.json</c> may hold passwords until it is applied, and %ProgramFiles% lets every user READ:
    /// the file is given its own rights — SYSTEM and the Administrators only, nothing inherited. Re-applied at
    /// every <c>apply-config</c>, because an editor that saves by REPLACING the file brings the folder's back.
    /// The service never reads it: what it must do under its identity is handed to it (<c>pending\</c>).
    /// </summary>
    public static void RestrictToAdministrators(string file)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(file)) return;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }

    public static AgentHome ForThisMachine()
    {
        var configured = Environment.GetEnvironmentVariable("PAYROLL_AGENT_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return new AgentHome(configured);
        if (OperatingSystem.IsWindows())
        {
            return new AgentHome(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IGF", "PayrollSageAgent"));
        }
        return new AgentHome(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".payroll-sage-agent"));
    }

    /// <summary>Creates the folders: on Windows, the root with its restricted rights; on Unix, owner only.</summary>
    public void Ensure()
    {
        if (OperatingSystem.IsWindows()) CreateRestrictedRoot();
        Directory.CreateDirectory(Bindings);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Pending);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            foreach (var folder in new[] { Root, Bindings, Logs, Pending }) File.SetUnixFileMode(folder, owner);
        }
    }

    /// <summary>
    /// The root, created with no inherited right: SYSTEM and Administrators in full, the service's virtual
    /// account in modify, everything below inheriting it. An existing root is left as it is — the
    /// installer set it, and re-applying here would undo a deliberate change by IT.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void CreateRestrictedRoot()
    {
        if (Directory.Exists(Root)) return;
        const InheritanceFlags below = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (who, rights) in new[]
        {
            (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl),
            (new SecurityIdentifier(ServiceIdentity.Sid(ServiceIdentity.Name)), FileSystemRights.Modify),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(who, rights, below, PropagationFlags.None, AccessControlType.Allow));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        new DirectoryInfo(Root).Create(security);
    }
}
