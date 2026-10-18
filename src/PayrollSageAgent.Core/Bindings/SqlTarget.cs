namespace PayrollSageAgent.Core.Bindings;

/// <summary>
/// Where ONE binding reads: one SQL Server instance, ONE database, and how it signs in
/// (.claude/rules/sage-sql-access.md).
///   <c>sql</c>      a SQL Server login — <see cref="User"/>, its password protected with the binding;
///   <c>windows</c>  the SERVICE's own Windows identity — <c>NT SERVICE\PayrollSageAgent</c> on this machine,
///                   the machine account <c>DOMAINE\SERVEUR$</c> towards another server of a domain. No
///                   password exists; <see cref="User"/> is empty.
/// A binding file written before 2026-10-17's 0.3.0 has no <c>auth</c>: it reads as <c>sql</c>.
/// </summary>
public sealed record SqlTarget(string Server, string Database, string User, bool TrustServerCertificate = false, string Auth = SqlAuth.Sql)
{
    public bool IsWindows => Auth == SqlAuth.Windows;
}

public static class SqlAuth
{
    public const string Sql = "sql";
    public const string Windows = "windows";
}
