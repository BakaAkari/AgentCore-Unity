# Security Policy

## Reporting a vulnerability

Please report security issues privately via GitHub's
["Report a vulnerability"](https://github.com/BakaAkari/AgentCore-Unity/security/advisories/new)
feature (Security tab → Advisories) rather than filing a public issue.

This is a single-maintainer project — response time is best-effort, not
SLA-backed.

## Known risk surface (read before enabling in a shared/production project)

AgentCore Unity is an AI agent with tool-calling access to the Unity Editor.
By design, several of its capabilities are high-risk if misused or if the
connected LLM is untrusted or compromised:

- **`execute_code`**: executes arbitrary C# inside the Editor process via
  Mono.CSharp. This is equivalent to arbitrary code execution with your
  Editor's full privileges (file system, network, project assets). It is
  gated behind the tool confirmation system by default (`Restricted`
  visibility, requires confirmation unless a session trust scope — Trust
  Low/Med or YOLO — has been granted), but a user who grants broad trust
  scope to a misbehaving or compromised LLM endpoint has no further safety
  net at the tool layer.
- **Play Mode write actions**: gated by an explicit allow-list
  (`PlaymodeRuntimeSafeActions`, fail-closed as of v1.14.0) intended to keep
  Play Mode edits in-memory-only (reverted on Stop). This allow-list has had
  at least one real gap discovered and fixed post-release (see CHANGELOG
  v1.14.0) — treat it as a mitigation, not a guarantee, especially in
  versions prior to v1.14.0.
- **LLM endpoint trust**: this project sends project context (file contents,
  Console output, scene/asset state) to whatever OpenAI-compatible endpoint
  you configure. If you point it at a third-party or self-hosted endpoint,
  you are trusting that endpoint's operator with that data. No exfiltration
  protections exist beyond what you configure at the network level.
- **Destructive operations**: delete/remove/destroy-class actions require
  confirmation by default; other write actions may not, depending on
  configured risk policy. Review `Editor/Tools/Safety/ToolRiskPolicy` and
  your Trust Scope settings before enabling YOLO mode on a project without
  version control or backups.

## Recommended posture for shared/team/production projects

- Keep the project under version control (Git/SVN/Perforce) before enabling
  write-capable tools.
- Do not grant session-wide "YOLO" trust on projects you cannot easily revert.
- Review the LLM endpoint you connect — prefer endpoints you control or trust
  with your project's source and Console data.
- Treat `execute_code` as equivalent to giving the connected LLM a shell.
