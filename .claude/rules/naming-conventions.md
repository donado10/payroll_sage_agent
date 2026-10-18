# Naming

**English identifiers, French sentences.** Types, members, files, config keys — English. Every
sentence a person reads — CLI output, the event log, an error shown by the platform — French.

- C#: the .NET conventions. `PascalCase` types and members, `camelCase` locals and parameters,
  `_camelCase` private fields, file-scoped namespaces, one public type per file named after it.
- Namespaces follow the project: `PayrollSageAgent.Core.*`, `PayrollSageAgent.Sage.*`,
  `PayrollSageAgent.Service.*`.
- **Use the contract's words.** A *binding* is a binding, a *job* a job, a *pairing code* a pairing
  code — the names `platform/protocol.md` uses, so a grep finds both sides.
- **Sage's names stay Sage's.** `F_COMPTEG`, `CG_Num`… are not « cleaned up » into English inside a
  query or a mapping: the mapping is where a Sage column becomes a contract field, and both names
  must be visible there.
- A test method says what it proves, as a sentence: `The_platform_still_serves_the_protocol_this_agent_speaks`.
