# Warning: I have been loop engineered — but with a lot of guidance.

Written by an agent working in a loop, steered closely by a human the whole way: every feature was specified, reviewed and corrected by hand. That is not the same as being battle-tested. Treat this as a proof of concept.

# GrumpyGit

A diff-first Git client for Windows, built with .NET 9 and Avalonia. The commit graph, staging and push/pull are all here — but the thing it actually cares about is making a change easy to *see*.

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![License](https://img.shields.io/badge/license-Apache%202.0-green)
![.NET](https://img.shields.io/badge/.NET-9.0-purple)
[![Grumpy](https://img.shields.io/github/v/release/garethrepton/GrumpyGit?filter=v*&label=Grumpy&color=blue)](https://github.com/garethrepton/GrumpyGit/releases/latest)
[![Grumpy AI](https://img.shields.io/github/v/release/garethrepton/GrumpyGit?filter=ai-v*&label=Grumpy%20AI%20(experimental)&color=8957e5)](https://github.com/garethrepton/GrumpyGit/releases?q=ai-v&expanded=true)

## Download — two products, two release chains

**Grumpy is a git client with no AI in it, and that is not going to change.** The
local model lives in a separate product with its own release chain, so you only
get it if you deliberately go and download it. Grumpy will never grow it in an
update, and installing Grumpy AI will not replace or upgrade an existing Grumpy —
different installer, different install folder, different entry in Apps & features.
Run one, or run both side by side.

> ### ⚠️ Grumpy AI is experimental
>
> Its review is a **suggestion, not a verdict** — it will miss real bugs and flag
> things that are fine. Read the diff yourself; nothing here replaces that.
> Behaviour, settings and the model catalogue may change between releases.
>
> The git side is not experimental: every git operation is the same code as
> Grumpy. It is the review pass on top that is new. If you want a client to rely
> on, **install Grumpy**.

| | **Grumpy** | **Grumpy AI** |
|---|---|---|
| Status | Stable | **Experimental** — read the warning above |
| What it is | The git client | The git client **plus** an offline local model that reads the diff and flags what looks risky |
| Download | **[⬇ Latest Grumpy release](https://github.com/garethrepton/GrumpyGit/releases/latest)** | **[⬇ Latest Grumpy AI release](https://github.com/garethrepton/GrumpyGit/releases?q=ai-v&expanded=true)** |
| Release chain | `v<version>` | `ai-v<version>` — versioned independently |
| Installer | `Grumpy-<version>-win-x64-setup.exe` | `Grumpy-AI-<version>-win-x64-setup.exe` |
| Portable zip | `Grumpy-<version>-win-x64-portable.zip` → run `Grumpy\Grumpy.exe` | `Grumpy-AI-<version>-win-x64-portable.zip` → run `GrumpyAI\Grumpy.exe` |
| winget | `winget install GarethRepton.GrumpyGit` | `winget install GarethRepton.GrumpyGit.AI` |
| Installs to | `%LOCALAPPDATA%\Programs\Grumpy` | `%LOCALAPPDATA%\Programs\Grumpy AI` |
| Shows up as | Grumpy | Grumpy AI **(Experimental)** — in the window title, the Start menu and Apps & features |
| Icon | The sheep | The sheep with a violet **AI** badge |
| Model runtime | **None.** Every release is checked file by file before it is published | Bundled — a much larger download |

Both are per-user installs: no administrator rights, no UAC prompt, uninstall
from **Apps & features** like any other app. Both are self-contained, so no .NET
runtime install is required.

**Even Grumpy AI ships no model weights.** You pick one from a short in-app
catalogue and press download. It then runs in-process on your machine, and your
code never leaves it.

Settings, recent repositories and review notes live in `%LOCALAPPDATA%\Grumpy`
for **both** products, so they follow you if you switch or run both. That also
means deleting that folder from one uninstaller takes the other's notes with it —
the uninstaller says so before it does anything.

## Reading a change

Four ways to look at the same diff, switched from the diff toolbar. They are all readings of one parsed diff — swapping between them never re-runs git.

- **Split** — the classic two panes, old on the left and new on the right, with character-level highlighting of what actually differs within a changed line.
- **Ghost** — one column. You read the file as it now stands, with the lines each edit displaced left in place above their replacements, dimmed and struck through. No eye ping-pong between panes.
- **Blink** — one pane holding one scroll position, flipped between old and new with the **space** bar. Structural change registers as movement rather than as colour, which is startlingly good at catching things you would otherwise scroll past.
- **Moved** — detects blocks removed in one place and re-added in another and colours them as *moved*, instead of an unrelated deletion plus an unrelated insertion. Turns a big refactor into a handful of real edits.

**Changed symbols** sits above the diff: what the change touches, by function rather than by line range, with the line budget each one accounts for. Click an entry to jump straight to it. It reads the enclosing declaration from git's own hunk headers, so it works for every language git ships a diff driver for — no parser, no language server. Grumpy supplies sensible driver defaults, and a repository's own `.gitattributes` still wins.

Supporting the above:

- **Full file by default**, with long unchanged runs folded behind expanders — a change reads better with its surroundings available.
- **Opens on the first change** rather than at line 1, in every mode.
- Whitespace-insensitive diffing, adjustable context, a change minimap, and syntax highlighting via TextMate grammars.

## The rest of the client

- **Pull request preview** (**Ctrl+Shift+R**) — review your branch against the one it will merge into, before raising anything. Shows the commits the merge would introduce, the net diff file by file, and whether it would actually conflict — a real merge simulated in memory, without switching branches or touching your working tree. Tick files off, write notes against them, then copy the whole review out as markdown. Entirely local: no hosting provider is contacted, and nothing is pushed.
- **Commit graph** — the full branching history as an interactive DAG
- **Unpushed at a glance** — commits that exist on no remote are badged in the graph and counted in the status bar, so you can see what a push would publish
- **Working tree** — stage and unstage whole files, individual hunks or individual lines, then commit
- **Branches and tags** — switch, create, merge and tag from the toolbar; pushing carries annotated tags with it
- **Push / Pull** — any remote, via Git Credential Manager (no OAuth setup needed)
- **Blame** and **image diffs** for files where a text diff says nothing

## Requirements

- [Git for Windows](https://git-scm.com/download/win) — must be on `PATH`
- .NET 9 Runtime (or build from source with the SDK)

## Getting Started

```bash
git clone https://github.com/garethrepton/GrumpyGit.git
cd GrumpyGit
dotnet run --project src/GrumpyGit.App
```

Then click **Open Repo** in the toolbar and select any local git repository.

## Technology

| Concern | Choice |
|---|---|
| UI Framework | [Avalonia](https://avaloniaui.net/) 11 (Skia-backed, cross-platform capable) |
| Git backend | Shell out to `git.exe` via [CliWrap](https://github.com/Tyrrrz/CliWrap) |
| Diff viewer | [AvaloniaEdit](https://github.com/avaloniaui/avaloniaedit) + [TextMateSharp](https://github.com/danipen/TextMateSharp) |
| MVVM | [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) |

All git operations go through `git.exe` — SSH, Git Credential Manager, and every git feature work out of the box with no custom implementation.

## Project Structure

```
GrumpyGit/
├── src/
│   ├── GrumpyGit.App/        # Avalonia application (views, viewmodels, controls)
│   └── GrumpyGit.Core/       # Domain logic (git operations, diff parsing, graph layout)
└── tests/
    └── GrumpyGit.Core.Tests/ # Unit tests for core logic
```

## License

Apache 2.0 — see [LICENSE](LICENSE).
