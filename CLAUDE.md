# GrumpyGit — Visual Git Client

A .NET desktop application providing a visual git client with GitHub integration. The goal is a first-class visual experience for browsing history, reviewing diffs, staging changes, and pushing to GitHub.

## What This App Does

- Connect to a local or GitHub-hosted git repository
- Render the commit history as an interactive DAG graph (branching tree)
- Click a commit to see the files changed in that commit
- Click a file to see the line-by-line diff (syntax highlighted)
- View uncommitted local changes (working tree + index)
- Stage whole files or individual hunks and commit
- Push / pull to/from GitHub (via Git Credential Manager — no custom OAuth UI needed)
- Compare the full codebase between any two commits (spanning all commits in between)

---

## Technology Stack

| Concern | Choice | Package |
|---|---|---|
| UI Framework | Avalonia (Skia-backed, Windows-first but cross-platform capable) | `Avalonia`, `Avalonia.Desktop` |
| All git operations | Shell out to `git.exe` via CliWrap | `CliWrap` |
| GitHub API (PRs, OAuth token, repo info) | Octokit.NET | `Octokit` |
| Diff computation | DiffPlex | `DiffPlex` |
| Code editor / diff viewer | AvaloniaEdit + TextMate grammars | `Avalonia.AvaloniaEdit`, `AvaloniaEdit.TextMate`, `TextMateSharp.Grammars` |
| Commit graph rendering | Custom Avalonia `DrawingContext` (pvigier lane-assignment algorithm) | — |
| MVVM | CommunityToolkit.Mvvm | `CommunityToolkit.Mvvm` |

### Key Architecture Decisions

**100% CLI git backend:** All git operations go through `git.exe` via CliWrap. This is the same approach as GitHub Desktop, GitKraken, and Sourcetree. It gives us every git feature with zero library gaps — SSH works, hunk-level staging works, and Git Credential Manager handles GitHub OAuth automatically. Use `--porcelain` and machine-readable output formats for reliable parsing.

**Git Credential Manager:** Ships with Git for Windows and intercepts credential requests for push/pull automatically. No OAuth browser flow or token management code needed in this app.

**Hunk-level staging:** Use `git add -p` piped via CliWrap, or construct a patch string from selected hunks in the UI and pipe to `git apply --cached` via stdin.

**Commit graph data:** Use `git log --format="%H%x00%P%x00%an%x00%ae%x00%ai%x00%s" --all` (null-delimited fields) for reliable parsing. Apply the pvigier lane-assignment algorithm to compute column positions. Render using Avalonia's `DrawingContext` on a virtualised canvas (only visible rows rendered).

**Diffs:** Use `git diff <commit> -- <file>` or `git diff <commitA>..<commitB>` and pipe the unified diff output to DiffPlex for structured parsing, then render via AvaloniaEdit with syntax highlighting.

**Status:** Use `git status --porcelain=v2` — the v2 format is stable, machine-readable, and includes rename/copy tracking.

---

## Project Structure

```
GrumpyGit/
├── GrumpyGit.sln
├── src/
│   ├── GrumpyGit.App/           # Avalonia application entry point
│   ├── GrumpyGit.Core/          # Domain logic: git operations, models
│   │   ├── Git/                 # CliWrap wrappers for git.exe commands
│   │   ├── Graph/               # Commit graph lane-assignment algorithm
│   │   └── Models/              # CommitNode, FileChange, DiffHunk, etc.
│   └── GrumpyGit.UI/            # Avalonia views, viewmodels, controls
│       ├── Controls/            # CommitGraph canvas, DiffViewer, etc.
│       └── ViewModels/          # MVVM viewmodels
└── tests/
    ├── GrumpyGit.Core.Tests/
    └── GrumpyGit.UI.Tests/
```

---

## Available Agents

Use these agents in parallel when working on independent tasks. Invoke multiple agents simultaneously where possible. These agents are available in the global agent store.

### `dotnet-tool`
Use for: scaffolding projects, adding NuGet packages, running builds and tests, project configuration, `dotnet` CLI tasks.
Examples: creating the solution structure, adding a NuGet package, running `dotnet build`, setting up test projects.

### `planning-agent`
Use for: designing implementation plans before writing code. Give it a feature description and it returns a step-by-step implementation plan.
Use this before starting any non-trivial feature (commit graph algorithm, diff viewer, staging UI).

### `security-reviewer`
Use proactively after implementing any feature that touches: file paths, git credentials, network calls, API keys, or user-provided input (repo URLs, commit messages).
FLAGS dangerous patterns — treat its output as a blocking review.

### `network-audit`
Use proactively after any code changes that could introduce outbound network calls. Run in parallel with `security-reviewer` after completing a feature.

### `dangerous-code`
Use before committing. Scans for patterns inappropriate for production: hardcoded credentials, debug backdoors, unsafe operations.

### `package-audit`
Use when adding or updating NuGet packages. Checks for security issues, popularity, and known vulnerabilities.

### `git-agent`
Use for: all git workflow operations within this project — branching, committing (Conventional Commits format), pull requests, merging, tagging releases.
Follows the branching strategy: `main` → production, `develop` → integration, `feature/*` → work branches.

### `obvious-bug`
Use to find obvious bugs by understanding the application's intent and identifying things that are not connected up, wired in, or reachable.

### `app-summariser`
Use when you need a high-level summary of what the app or a module does — useful for onboarding context or documentation.

---

## Development Workflow

1. Branch from `develop`: `feature/<description>`
2. Before committing, run `dangerous-code` + `security-reviewer` + `network-audit` in parallel
3. Use `git-agent` to commit (Conventional Commits), raise a PR into `develop`
4. Use `package-audit` when adding any new NuGet dependency

---

## Known Constraints & Gotchas

- **git.exe must be installed** — this is a safe assumption for any git client user
- **Always use machine-readable formats** — `--porcelain=v2` for status, `--format=...` with null delimiters for log; never parse human-readable output
- **Binary file diffs** — show size delta only, no content diff
- **WinUI 3 is explicitly ruled out** — do not suggest it; its future investment from Microsoft is uncertain
- **Never use `git add .` or `git add -A`** — always stage specific files by name
- **Avalonia over WPF** — Avalonia keeps cross-platform options open and has better rendering architecture
- **LibGit2Sharp is explicitly ruled out** — do not suggest it; 100% CliWrap is the chosen approach
