using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PayrollSageAgent.Core;

/// <summary>
/// Who the agent is on Windows: the service <see cref="Name"/>, and the account it runs as — the
/// virtual account <c>NT SERVICE\PayrollSageAgent</c>, which Windows creates with the service, needs no
/// password and holds no right but those it is given (here: its own folder).
///
/// <see cref="Sid"/> is that account's SID, computed as <c>sc showsid</c> does — <c>S-1-5-80-</c> then
/// the SHA-1 of the upper-cased name in UTF-16, as five little-endian integers — so the agent's folder
/// can name it BEFORE the service exists, and with no account name to translate (a French Windows says
/// « Administrateurs », never « Administrators »). Checked against TrustedInstaller's published SID.
/// </summary>
public static class ServiceIdentity
{
    public const string Name = "PayrollSageAgent";

    public static string Sid(string serviceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "S-1-5-80-" + string.Join('-', parts);
    }
}
