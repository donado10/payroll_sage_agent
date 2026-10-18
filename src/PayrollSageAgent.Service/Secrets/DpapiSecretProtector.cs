using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Service.Secrets;

/// <summary>
/// Windows: DPAPI, MACHINE scope — the service (its own account) must read what <c>add-binding</c>
/// (an administrator) wrote. Machine scope means any process on this machine could ask DPAPI to
/// decrypt; what stops it is the folder's ACL (SYSTEM, Administrators, the service account), which the
/// installer sets (Phase 7). [HARD] Written and compiled here, NOT run: this machine is Linux.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IGF.PayrollSageAgent.binding.v1");

    public string Kind => "DPAPI";

    public string Protect(string secret) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine));

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Ce secret n’a pas été protégé par DPAPI sur cette machine.");
        }
        var bytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine));
    }
}
