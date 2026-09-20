# Canonical Application Version & Build Architecture

## Active Canonical Release Binary
- **Executable**: `src\WhoseIptv.Package\bin\x64\Release\IptvPlayer.App\IptvPlayer.App.exe` (SHA-256: `6792D2B6F3F49670B2C062766FF29182F20E9C908604F528D0726B271EF4FC16`)
- **Assembly**: `src\WhoseIptv.Package\bin\x64\Release\IptvPlayer.App\IptvPlayer.App.dll` (SHA-256: `4FBD015CA15337043C3ACE7DC0113A14F6E9AFE321C7389A698121E3F716FEEA`)
- **Presentation Assembly**: `src\WhoseIptv.Package\bin\x64\Release\IptvPlayer.App\IptvPlayer.Presentation.dll` (SHA-256: `D43A5E888F9FC7B903514D745439B0E65874FBA31A6BFF9F02389E299C14F3A1`)
- **Version**: `1.0.18.0` (Microsoft Store Official Release)
- **Visual Assets**: Authentic neon-blue metallic TV logo & assets matching Microsoft Store `1.0.18.0` release bit-for-bit
- **Playback Architecture**: 100% embedded in-app VLC rendering with zero detached windows.

## Store Packaging Pipeline
- **Package Project**: `src\WhoseIptv.Package\WhoseIptv.Package.wapproj`
- **Installed Package**: `WHOSEIPTV.WhoseIPTV_1.0.18.0_x64__jhywfcyt2h7f8`
- **Launch Script**: `Launch-Current-Version.cmd`

## STRICT ARCHITECTURAL INVARIANTS
1. **Never Recreate Stale Release Directories**:
   Do **not** create or target legacy directories such as `releases\current`, `releases\backup-before-opt`, or `qa-artifacts\store-package-*`.
2. **Single Source of Truth**:
   All development, feature additions, and updates MUST occur strictly within `src\`, and the executable launched for testing must always be `src\WhoseIptv.Package\bin\x64\Release\IptvPlayer.App\IptvPlayer.App.exe`.
3. **No Detached VLC Window**:
   `--vout` must NEVER be set to `direct3d11` or external top-level window. VLC HWND hosting must always remain strictly embedded inside `DirectVlcVideoHost`.
