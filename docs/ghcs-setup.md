# GitHub Copilot CLI (`ghcs`) Setup Guide

## Problem

The SpecKit Companion extension invokes `ghcs` (GitHub Copilot CLI) via terminal commands like:

```powershell
ghcs "$(cat "path\to\prompt.md")"
```

Four issues prevent this from working out of the box on Windows:

1. **`ghcs` is not a built-in command** — In `gh` CLI v2.80+, `gh copilot` is a built-in subcommand, but the `ghcs` shorthand alias does not exist.
2. **VS Code Insiders PATH conflict** — `gh copilot` searches PATH for a `copilot` binary and finds VS Code's `copilot.bat` at `C:\Users\<user>\AppData\Roaming\Code - Insiders\...\copilotCli\copilot.bat`. The space in `Code - Insiders` causes cmd.exe to break.
3. **Non-interactive mode** — `gh copilot` requires the `-p` flag for non-interactive (piped) prompts. SpecKit passes the prompt as a positional argument.
4. **Agent routing** — SpecKit Companion sends `/speckit.<agent> <args>` as prompt text, but `gh copilot` CLI doesn't interpret `/` prefixes as agent routing. It requires the `--agent <agent>` flag.
5. **Write permissions** — Non-interactive agent execution may still fail on file edits unless the CLI is granted path access. `--allow-all-tools` alone is insufficient for reliable writes; use `--allow-all` or at least `--allow-all-tools --allow-all-paths`.

## Prerequisites

Install GitHub CLI (`gh`):

```powershell
winget install --id GitHub.cli
```

After install, **restart your terminal** or refresh PATH:

```powershell
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
```

Authenticate:

```powershell
gh auth login
```

Verify:

```powershell
gh copilot -- --version
```

## Fix — Apply These Steps

### Step 1: Create `ghcs.ps1` wrapper

```powershell
$binDir = "$env:USERPROFILE\.local\bin"
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

Set-Content "$binDir\ghcs.ps1" @'
$savedPath = $env:Path
$env:Path = ($env:Path -split ';' | Where-Object { $_ -notlike '*copilotCli*' }) -join ';'
try {
    $prompt = "$args"
    if ($prompt -match '^/(\S+)\s*(.*)$') {
        $agent = $Matches[1]
        $text = $Matches[2]
        if ([string]::IsNullOrWhiteSpace($text)) { $text = '.' }
        gh copilot -p $text --agent $agent --allow-all
    } else {
        gh copilot -p @args
    }
} finally { $env:Path = $savedPath }
'@
```

This script:
- Temporarily removes VS Code's `copilotCli` directory from PATH (fixes the spaces-in-path error)
- Passes `-p` flag for non-interactive prompt mode
- Detects `/agent.name <text>` prefix and converts to `--agent agent.name --allow-all` flags
- Restores the original PATH after execution

### Step 1b: Add a `copilot` shim for SpecKit Companion

Some SpecKit Companion configurations invoke `copilot -p ...` directly instead of `ghcs`. In that case, patching `ghcs` alone will not affect the live execution path. Create a higher-precedence shim in `C:\Users\<user>\bin` so `copilot` resolves to the shim before the WinGet-installed executable.

Git Bash shim in `~/bin/copilot`:

```bash
#!/usr/bin/env bash
set -euo pipefail

REAL_COPILOT='/c/Users/<user>/AppData/Local/Microsoft/WinGet/Links/copilot'

if [[ "${1:-}" == "-p" && $# -ge 2 ]]; then
    prompt="$2"
    if [[ "$prompt" =~ ^/([^[:space:]]+)[[:space:]]*(.*) ]]; then
        agent="${BASH_REMATCH[1]}"
        text="${BASH_REMATCH[2]}"
        [[ -z "$text" ]] && text='.'
        exec "$REAL_COPILOT" -p "$text" --agent "$agent" --allow-all "${@:3}"
    fi
fi

exec "$REAL_COPILOT" "$@"
```

If you use PowerShell terminals for SpecKit, add a matching `copilot.ps1` shim in the same directory.

### Step 2: Add `~\.local\bin` to user PATH (one-time)

```powershell
$binDir = "$env:USERPROFILE\.local\bin"
$userPath = [System.Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$binDir*") {
    [System.Environment]::SetEnvironmentVariable("Path", "$userPath;$binDir", "User")
    Write-Host "Added $binDir to user PATH"
}
```

### Step 3: Add `ghcs` function to PowerShell profile (optional but recommended)

```powershell
Add-Content $PROFILE @'

function ghcs {
    $savedPath = $env:Path
    $env:Path = ($env:Path -split ';' | Where-Object { $_ -notlike '*copilotCli*' }) -join ';'
    try {
        $prompt = "$args"
        if ($prompt -match '^/(\S+)\s*(.*)$') {
            $agent = $Matches[1]
            $text = $Matches[2]
            if ([string]::IsNullOrWhiteSpace($text)) { $text = '.' }
            gh copilot -p $text --agent $agent --allow-all
        } else {
            gh copilot -p @args
        }
    } finally { $env:Path = $savedPath }
}
'@
```

The profile function takes priority over the `.ps1` file. Both are present for redundancy — the `.ps1` covers terminals launched with `-NoProfile`.

### Step 4: Restart VS Code / terminal

New terminals must be opened to pick up the PATH change and profile update.

## Verification

```powershell
# Should resolve to the function or .ps1 script
Get-Command ghcs

# Should respond without errors (regular prompt)
ghcs "say hello"

# Should invoke the speckit.specify agent (agent routing)
ghcs "/speckit.specify my new feature description"
```

## One-Liner (copy-paste all steps at once)

> **Note**: The one-liner below applies all three steps. For the full function logic (including agent routing), use the individual steps above.

## How It Works

When SpecKit Companion triggers a command like:

```powershell
ghcs "$(cat "path\to\prompt.md")"
```

The prompt file contains text like `/speckit.specify c:\code\project\specs\001-feature`. The `ghcs` function:

1. Strips `copilotCli` from PATH (avoids VS Code Insiders space-in-path error)
2. Detects the `/speckit.specify` prefix
3. Extracts the agent name (`speckit.specify`) and remaining text
4. Invokes `gh copilot -p "<text>" --agent speckit.specify --allow-all`
5. The CLI loads the agent definition from `.github/agents/speckit.specify.agent.md`

If SpecKit Companion invokes `copilot -p ...` directly, the shim in `~/bin/copilot` performs the same prefix translation and permission grant before delegating to the real executable.

## Notes

- The `gh copilot` extension (`gh extension install github/gh-copilot`) is **no longer needed** in gh CLI v2.80+ — copilot is built in.
- This fix is only needed on Windows where VS Code Insiders is installed (the `Code - Insiders` path with spaces triggers the issue).
- On macOS/Linux, a similar approach is needed for agent routing — a simple alias won't handle the `/agent` prefix parsing.
- Key `gh copilot` flags for reference:
  - `--agent <agent>` — invoke a custom agent from `.github/agents/`
  - `-p <text>` — non-interactive prompt mode
    - `--allow-all-tools` — allows tool use, but not broad filesystem access
    - `--allow-all-paths` — allows broader file write access
    - `--allow-all` — recommended for SpecKit; bundles the permissions needed for non-interactive agent edits
  - `-s` — silent mode (output only agent response, no stats)
