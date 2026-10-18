using PayrollSageAgent.Core.Update;

namespace PayrollSageAgent.Service.Update;

/// <summary>
/// Off Windows (development, in Docker): everything up to the install — check, download, verify — and NOT
/// the install, which is Windows Installer's. That is what lets the update be proven end to end here against a
/// real platform, as far as Linux goes.
/// </summary>
public sealed class DevelopmentUpdateMachine : IUpdateMachine
{
    public string? CannotInstall => "l’installation se fait sur Windows seulement (développement : rien n’est installé ici).";

    public string? InstalledVersion() => null;

    public string? OwnSigner() => null;

    public (bool Valid, string? Signer) Signature(string file) => (false, null);

    public Task<int> InstallAsync(string msi, string log, CancellationToken ct) => throw new PlatformNotSupportedException();

    public Task<int> UninstallAsync(string msi, string log, CancellationToken ct) => throw new PlatformNotSupportedException();

    public bool ServiceRunning() => false;

    public Task StartServiceAsync(CancellationToken ct) => Task.CompletedTask;
}
