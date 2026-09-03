# CADMCP repository rules

- `version.json` is the only manually maintained source for the CADMCP product, TCP protocol, and settings schema versions.
- Do not edit `version.json`, run `scripts/set-version.ps1`, or change any derived version unless the user explicitly provides the exact target version.
- Derived versions in `Directory.Build.props`, `plugin/VersionInfo.cs`, `bundle/PackageContents.xml`, and the server package manifests must be changed only through `scripts/set-version.ps1`.
- Product version changes apply to the AutoCAD plugin, CommandSet, Bundle, and npm Server together, even when one component has no functional code changes.
- Protocol and settings schema versions change only when their respective contracts change. A settings schema increment requires an explicit migration and tests.
- Run `scripts/test-version-consistency.ps1` after version-related work.

## Mandatory reading for tool/framework changes

- Before adding/changing a command, dispatcher, preview, selection, transaction, or undo behavior, read `CONTRIBUTING.md` and `docs/architecture/EXECUTION_CONTEXTS.md` completely. This is a correctness constraint, not an optional optimization.
- Every `ICadCommand` must explicitly declare `ExecutionKind`. Fixed reads and validated previews run on the AutoCAD application thread under a read lock; they must not enter the generic command context, acquire a default/write lock, reset selection, save, regenerate, or create an undo boundary.
- `set_selection` uses the separate selection path; commands return a selection intent instead of directly changing preselection. Preserve successful committed results if applying a post-commit selection fails.
- Real writes and arbitrary `send_code_to_cad` remain in command context. `transactionMode=none` is NOT read-only. Never infer read-only behavior from generated C# text or from an arbitrary client-supplied flag.
- Fixed create/clone/transform writes use the dispatcher's owned native command undo scope. Fixed creation declares FixedWrite, not PreviewableWrite. Do not nest framework ActiveX undo marks inside EXECUTEFUNCTION or bypass the dispatcher with a manually constructed context. Only confirm undoGuaranteed after command completion; keep committed results on finalization/selection failure.
- Dynamic C# is capability-first (ADR-003): both auto/none stay CommandContext with full permissions and undoGuaranteed=false. auto retains its database transaction; none stays self-managed. Never require strict UNDO state or a fixed-entity whitelist for dynamic execution, and never forbid user code from managing its own commands/transactions/undo. A false undo guarantee is not an execution failure.
- AutoCAD native test callbacks must catch exceptions internally. Explicitly activate and verify any newly created test document before writing; wait for a native U completion event, never wrap U in another command callback or retry an unknown U.
- Read/preview and write calls still share one execution slot, busy checks and document/space identity checks. Do not access CAD objects from TCP/worker threads.
- Text mirroring follows AutoCAD native behavior with the current MIRRTEXT setting; do not force reversed text when MIRRTEXT=0 or temporarily change the setting. Do not modify shared block definitions.
- New commands require MCP/schema/settings/docs integration, an execution-category test, and applicable host regression: write -> repeated reads/previews/selection -> ONE Undo. Check geometry and selection, not only DBMOD/entity count. Host compilation is not host acceptance.
- Keep past test reports as historical evidence; add follow-up results instead of rewriting past failures as passes. Do not auto-publish or commit unless asked.
