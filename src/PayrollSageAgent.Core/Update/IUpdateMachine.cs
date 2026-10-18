namespace PayrollSageAgent.Core.Update;

/// <summary>
/// What the updater needs from Windows — the MSI, the service, Authenticode. Behind an interface, because NONE
/// of it runs on this Linux machine (the repo's verification rule): the Windows implementation is written and
/// compiled, never executed here; the tests drive the updater through a fake.
/// </summary>
public interface IUpdateMachine
{
    /// <summary>Null when this machine can install; otherwise why not, in French (not Windows, not an MSI install).</summary>
    string? CannotInstall { get; }

    /// <summary>The version Windows Installer says is installed (the MSI's registry value), or null.</summary>
    string? InstalledVersion();

    /// <summary>Who signed THIS agent's exe (Authenticode subject), or null when it is not signed.</summary>
    string? OwnSigner();

    /// <summary>Whether <paramref name="file"/>'s Authenticode signature is valid, and by whom.</summary>
    (bool Valid, string? Signer) Signature(string file);

    /// <summary><c>msiexec /i</c>, silent, no restart — Windows Installer's exit code.</summary>
    Task<int> InstallAsync(string msi, string log, CancellationToken ct);

    /// <summary><c>msiexec /x … KEEPDATA=1</c> — the product removed, its bindings and secrets KEPT.</summary>
    Task<int> UninstallAsync(string msi, string log, CancellationToken ct);

    bool ServiceRunning();

    Task StartServiceAsync(CancellationToken ct);
}
