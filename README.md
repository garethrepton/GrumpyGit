# Warning: I have been vibe coded, this is just a POC nothing more, do not try to use me.

# GrumpyGit

A fast, keyboard-friendly visual Git client for Windows built with .NET 9 and Avalonia.

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![License](https://img.shields.io/badge/license-Apache%202.0-green)
![.NET](https://img.shields.io/badge/.NET-9.0-purple)

## Features

- **Commit graph** — browse the full branching history as an interactive DAG
- **Side-by-side diff viewer** — removed lines highlighted red (left), added lines highlighted green (right), with syntax highlighting via TextMate grammars
- **Working tree view** — see uncommitted changes, stage/unstage individual files, write a commit message and commit
- **Branch management** — switch branches, create new branches, merge from the toolbar
- **Push / Pull** — works with any remote via Git Credential Manager (no OAuth setup needed)

## Requirements

- [Git for Windows](https://git-scm.com/download/win) — must be on `PATH`
- .NET 9 Runtime (or build from source with the SDK)

## Getting Started

```bash
git clone https://github.com/your-org/GrumpyGit.git
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
