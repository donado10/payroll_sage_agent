using System.Security.Cryptography;
using System.Text;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Service.Secrets;

/// <summary>
/// [!] DEVELOPMENT ONLY, AND IT SAYS SO. On a Linux development machine there is no DPAPI: secrets are
/// sealed with AES-GCM under a key kept in a file beside them (<c>dev-secrets.key</c>, owner-only). Whoever
/// reads the agent's home reads the secrets — fine for the test container, never for a customer. It
/// refuses to exist on Windows, and anywhere unless <c>PAYROLL_AGENT_DEV_SECRETS=1</c> asks for it.
/// </summary>
public sealed class DevSecretProtector : ISecretProtector
{
    private const string Prefix = "dev1:";
    private readonly byte[] _key;

    public DevSecretProtector(AgentHome home)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Sur Windows, les secrets de l’agent sont protégés par DPAPI.");
        }
        home.Ensure();
        var keyFile = Path.Combine(home.Root, "dev-secrets.key");
        if (!File.Exists(keyFile))
        {
            File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _key = File.ReadAllBytes(keyFile);
    }

    public string Kind => "développement (non sûr)";

    public string Protect(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Ce secret n’a pas été protégé par la clé de développement de cette machine.");
        }
        var bytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
