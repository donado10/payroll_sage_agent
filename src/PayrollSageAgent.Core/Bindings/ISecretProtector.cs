namespace PayrollSageAgent.Core.Bindings;

/// <summary>
/// Protects a binding's secrets at rest. On Windows: DPAPI, machine scope. On a Linux development
/// machine: a stand-in that SAYS it is not secure (PayrollSageAgent.Service › DevSecretProtector).
/// </summary>
public interface ISecretProtector
{
    /// <summary>A short word for logs and listings: « DPAPI », « développement ».</summary>
    string Kind { get; }

    string Protect(string secret);

    string Unprotect(string protectedValue);
}
