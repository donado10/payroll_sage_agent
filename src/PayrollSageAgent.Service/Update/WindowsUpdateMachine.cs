using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using PayrollSageAgent.Core;
using PayrollSageAgent.Core.Update;

namespace PayrollSageAgent.Service.Update;

/// <summary>
/// The updater's Windows: Windows Installer (<c>msiexec</c>), the service, Authenticode (<c>WinVerifyTrust</c>).
/// [HARD] Written and compiled here, NEVER run here — this machine is Linux (verification.md). Its first run is
/// the user's Windows Server.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsUpdateMachine : IUpdateMachine
{
    private const string Key = @"SOFTWARE\IGF\PayrollSageAgent";

    /// <summary>An install by the MSI only: the zip's install.ps1 updates by being run again, from a newer zip.</summary>
    public string? CannotInstall => InstalledBy() == "msi"
        ? null
        : "l’installation automatique ne vaut que pour un agent installé par le MSI (celui-ci l’a été par install.ps1 : relancez-le depuis un zip plus récent).";

    public string? InstalledVersion() => Read("Version");

    private static string? InstalledBy() => Read("InstalledBy");

    private static string? Read(string name)
    {
        using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).OpenSubKey(Key);
        return key?.GetValue(name) as string;
    }

    public string? OwnSigner() => Environment.ProcessPath is { } exe ? SignerOf(exe) : null;

    public (bool Valid, string? Signer) Signature(string file) => (Trusted(file), SignerOf(file));

    public Task<int> InstallAsync(string msi, string log, CancellationToken ct) =>
        MsiexecAsync(["/i", msi, "/qn", "/norestart", "/l*v", log], ct);

    /// <summary>KEEPDATA=1: the MSI's purge stands down (its condition) — the bindings and their secrets stay.</summary>
    public Task<int> UninstallAsync(string msi, string log, CancellationToken ct) =>
        MsiexecAsync(["/x", msi, "/qn", "/norestart", "KEEPDATA=1", "/l*v", log], ct);

    public bool ServiceRunning()
    {
        try
        {
            using var service = new ServiceController(ServiceIdentity.Name);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task StartServiceAsync(CancellationToken ct)
    {
        try
        {
            using var service = new ServiceController(ServiceIdentity.Name);
            if (service.Status == ServiceControllerStatus.Running) return;
            if (service.Status != ServiceControllerStatus.StartPending) service.Start();
            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)), ct);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            // Said by the caller's next health read; the service's own recovery restarts it on failure.
        }
    }

    private static async Task<int> MsiexecAsync(string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("msiexec n’a pas démarré.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(30));
        await process.WaitForExitAsync(limit.Token);
        return process.ExitCode;
    }

    /// <summary>The Authenticode signer's subject, or null for an unsigned file.</summary>
    private static string? SignerOf(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No X509CertificateLoader equivalent reads an Authenticode signature.
            using var certificate = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ---- WinVerifyTrust: the signature is valid and chains to a root this machine trusts. ----------------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Size;
        public nint Path;
        public nint Handle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;           // 2: WTD_UI_NONE
        public uint RevocationChecks;   // 0: WTD_REVOKE_NONE — a customer's server may not reach a CRL
        public uint UnionChoice;        // 1: WTD_CHOICE_FILE
        public nint File;
        public uint StateAction;        // 1: VERIFY, then 2: CLOSE
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    private static bool Trusted(string file)
    {
        var path = Marshal.StringToHGlobalUni(file);
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path }, info, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = info, StateAction = 1 };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(-1, ref action, ref data);
            data.StateAction = 2;
            WinVerifyTrust(-1, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(path);
        }
    }
}
