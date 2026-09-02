# CADMCP repository rules

- `version.json` is the only manually maintained source for the CADMCP product, TCP protocol, and settings schema versions.
- Do not edit `version.json`, run `scripts/set-version.ps1`, or change any derived version unless the user explicitly provides the exact target version.
- Derived versions in `Directory.Build.props`, `plugin/VersionInfo.cs`, `bundle/PackageContents.xml`, and the server package manifests must be changed only through `scripts/set-version.ps1`.
- Product version changes apply to the AutoCAD plugin, CommandSet, Bundle, and npm Server together, even when one component has no functional code changes.
- Protocol and settings schema versions change only when their respective contracts change. A settings schema increment requires an explicit migration and tests.
- Run `scripts/test-version-consistency.ps1` after version-related work.
