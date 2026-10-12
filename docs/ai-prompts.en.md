# CodeAtlas prompts for AI assistants

[繁體中文版本](ai-prompts.zh-TW.md)

Copy a prompt below and replace the `<...>` placeholders. First confirm that the AI
assistant can access your local project.
These prompts do not imply that CodeAtlas provides MCP access or automatic AI trace
reading. If no actual trace-reading tool is available, supply redacted viewer
evidence or collector output. The assistant must not pretend it has inspected traces.

## Prompt 1: Integrate into an existing project

```text
Integrate CodeAtlas Live into <absolute project path> for local Debug/Development only.
The CodeAtlas source repository is at <absolute codeatlas-live path>.
First read the applicable AGENTS.md files in both repositories, the project's
PLAN/architecture documents, and CodeAtlas's README.md, docs/integration.md,
docs/limitations.md, and docs/usage.zh-TW.md.
Check git status first, preserve unrelated changes, and follow the project's
branch and task rules.
If CodeAtlas is already integrated, verify it and fill only the gaps; do not add
duplicate exporters, services, or dependencies.
Change only the necessary startup, build, and dev-server configuration. Do not
modify business modules or hand-edit generated files.
Guard backend references and calls with Debug conditions and compilation guards;
production builds must contain no CodeAtlas runtime.
Initialize the frontend runtime only in development. Proxy only the base-aware
trace ingestion endpoint, never management APIs such as session-token or open-file.
Bind the collector to loopback.
Preserve existing permissions, validation, OpenTelemetry sampling, exporters,
and enrichment behavior.
Ensure source-root points to the observed application. If code-server is used,
handle absproxy, base paths, and HMR correctly.
The CodeAtlas repository and release packages are public. Do not expose the
application's repository, running collector, or traces without explicit authorization. Do not ask me
to paste tokens.
Run the relevant tests and Release isolation checks, and report automated results
separately from manual UI acceptance.
Do not push, merge, deploy, or modify production services without explicit authorization.
```

## Prompt 2: Debug using execution evidence

```text
Investigate <problem description> in the project at <project path>.
Reproduction steps: <steps>; expected behavior: <expected>; actual behavior: <actual>.
CodeAtlas evidence: <redacted span/trace data, error messages, or viewer screenshots>.
First read the applicable repository instructions and CodeAtlas's integration
and limitations documents.
Distinguish recorded execution, routing metadata, heuristic correlation, and
exception stack traces.
Treat only recorded spans as execution evidence. Mark unrecorded methods or call
chains as unknown; do not invent missing steps.
Tie each conclusion to an actual trace/span or code path, and list hypotheses
that still need verification.
Do not treat source metadata as proof that a method body executed, or temporal
proximity as proof of causation.
If you cannot read the collector directly, state what evidence is missing; do not
claim that you have inspected traces.
Reproduce the problem and confirm the root cause before making a minimal fix.
Add appropriate tests and preserve security and Release isolation.
Do not send raw traces, exceptions, headers, SQL, tokens, or sensitive business
data to external services.
Report in English: root cause, evidence, changes, tests, and remaining manual checks.
```

## Prompt 3: Verify and publish a release

```text
Verify CodeAtlas Live <version> and publish it as a Release in the public repository
<owner>/codeatlas-live.
First check repository visibility, remotes, git status, package versions, and
sensitive data. Confirm the intended public repository and stop if secrets or private application data are found.
Read scripts/test-all.sh and docs/manual-checks.md. Run the checks that are
available and accurately report any outstanding manual checks.
Build packages with scripts/pack-local.sh. Upload only the NuGet nupkg files
and npm tgz tarball to the Release.
Do not publish to public NuGet/npm registries, change repository visibility,
or write credentials into documentation.
Never silently overwrite an existing tag or asset. The release tag must point
to the exact commit that was verified.
After publishing, verify public visibility, the tag commit, and asset names.
Download the assets and compare SHA-256 checksums against the local packages.
Update the usage documentation and AI prompts. Clearly distinguish registry
publication from file distribution through a GitHub Release.
```
