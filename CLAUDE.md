# GrumpyGit — Visual Git Client

A .NET desktop application providing a visual git client. The goal is a first-class visual experience for browsing history, reviewing diffs, staging changes, and pushing.

## What This App Does

- Connect to a local git repository
- Render the commit history as an interactive DAG graph (branching tree)
- Click a commit to see the files changed in that commit
- Click a file to see the line-by-line diff (syntax highlighted)
- View uncommitted local changes (working tree + index)
- Stage whole files or individual hunks and commit
- Create, clone, fetch, push and pull (via Git Credential Manager — no custom auth UI needed)
- Compare the full codebase between any two commits (spanning all commits in between)
- Preview a pull request locally: diff a branch against its merge base with the target, simulate the
  merge to find conflicts, review file by file, and copy the review out as markdown

---

## The Commandments

These govern all work in this repo. They are not preferences — a change that breaks one is a
decision to raise, not to make quietly.

1. **Thou shalt not call out unless it has explicitly been agreed.** The agreed outbound surface is
   exactly two things: `git.exe` push, pull, fetch and clone, credentials handled by Git Credential
   Manager; and **one user-pressed model download** from the hard-coded `ModelCatalogue`, all on
   one host, every entry published by the model's own vendor and verified against a published
   SHA-256. (Fetch and clone were added on
   2026-08-07, on request — `Scans/2026-08-07-repo-operations.html`; the model download the same
   day — `Scans/2026-08-07-model-download.html`; the catalogue grew from five entries to nine, and
   then to twelve with the Gemma 4 additions, on 2026-08-08, on request —
   `Scans/2026-08-08-model-library.html`. Every entry is on huggingface.co under the model
   vendor's own organisation.) Nothing
   else — no API client, no telemetry, no update check, no analytics, no third party endpoint —
   gets added on your own initiative, however convenient. Ask, get a yes, then write it. Silence is
   a no. Run `network-audit` after any change that could have added one. A GitHub API client via
   Octokit was once part of this surface and was **deliberately removed**; adding one back is a new
   decision to be asked for, not a restoration.
2. **Thou shalt keep outbound calls neatly abstracted.** Everything that touches the outside world —
   `git.exe`, the shell, the filesystem — lives behind its own type in its own file under
   `GrumpyGit.Core/`, so the boundary is one grep away and viewmodels stay testable without a real
   repository. No raw `CliWrap` or `Process.Start` calls scattered through UI or viewmodel code.
3. **Thou shalt use the minimal amount of tools and code to achieve the goal.** Prefer no new
   dependency, then a smaller one; prefer extending an existing type to adding one. The stack in
   this file is deliberately short — every addition to it is a decision, so run `package-audit` and
   justify it. Delete rather than deprecate.
4. **Thou shalt keep to SOLID where it earns its keep, and go functional where that is clearer.**
   Interfaces exist to be seams for testing — a fake git backend — not for
   symmetry. A pure static function over immutable records beats a class hierarchy when there is no
   state to hold; graph lane assignment and diff parsing are pure transforms and should read as such.
5. **Thou shalt allow no security concerns.** This must be safe to run in a production environment
   without a second thought: no credential or token written to disk or logged (Git Credential
   Manager owns that), no repo path, branch name, commit message or remote URL interpolated into a
   shell — pass arguments as arguments, never build a command line, and never trust a value that
   came from a repository. Treat `security-reviewer` and `dangerous-code` output as a blocking
   review, not advice.
6. **Thou shalt not comment everywhere without a good reason.** A comment earns its place by saying
   *why* — the surprising constraint, the bug that motivated the code, the obvious approach that was
   tried and failed (git's porcelain formats are full of these). Never restate what the code already
   says; a line that narrates the obvious is noise that rots the moment the code moves. If a comment
   is needed to explain *what* is happening, rename or restructure instead.
7. **Thou shalt not add packages we do not need.** The stack table below is the whole list, and it
   is short on purpose. A package is not free: it is transitive dependencies, a licence, a supply
   chain, an upgrade treadmill, and — for a desktop app — startup time and installer size. Reach for
   one only when the alternative is genuinely reimplementing something hard, run `package-audit`
   before adding it, and say why in the PR. A helper you would use twice is a function, not a
   reference.
8. **Thou shalt be succinct — in explanations, comments and code.** Answer, then stop. No preamble,
   no recap of what was just read, no summary of a summary. The same applies to the artefacts: a
   viewmodel that needs a paragraph to explain itself needs splitting instead, and a commit message
   says what changed and why in a line. Short is a courtesy to the next reader, human or model.
9. **Thou shalt not store, commit or leak secrets or PII.** No token, credential, connection string,
   machine name or user path in source, tests, fixtures, logs or settings — Git Credential Manager
   holds the credentials and nothing here caches them. A git repository is full of real people's
   names and email addresses: they belong on screen, not in logs, telemetry or crash reports, and
   never in test fixtures. Invent test repositories; never paste a real one's history.

---

## Technology Stack

| Concern | Choice | Package |
|---|---|---|
| UI Framework | Avalonia (Skia-backed, Windows-first but cross-platform capable) | `Avalonia`, `Avalonia.Desktop` |
| All git operations | Shell out to `git.exe` via CliWrap | `CliWrap` |
| Diff computation | DiffPlex | `DiffPlex` |
| Code editor / diff viewer | AvaloniaEdit + TextMate grammars | `Avalonia.AvaloniaEdit`, `AvaloniaEdit.TextMate`, `TextMateSharp.Grammars` |
| Commit graph rendering | Custom Avalonia `DrawingContext` (pvigier lane-assignment algorithm) | — |
| MVVM | CommunityToolkit.Mvvm | `CommunityToolkit.Mvvm` |
| Local diff review (optional) | llama.cpp in-process, GGUF supplied by the user — no weights shipped, none downloaded | `LLamaSharp`, `LLamaSharp.Backend.Cpu` |

### Key Architecture Decisions

**100% CLI git backend:** All git operations go through `git.exe` via CliWrap. This is the same approach as GitHub Desktop, GitKraken, and Sourcetree. It gives us every git feature with zero library gaps — SSH works, hunk-level staging works, and Git Credential Manager handles authentication automatically. Use `--porcelain` and machine-readable output formats for reliable parsing.

**Git Credential Manager:** Ships with Git for Windows and intercepts credential requests for push, pull, fetch and clone automatically. There is no authentication, token or credential code in this app at all — that is the point, and it is the position to defend.

**No hosting-provider API:** This is a git client, not a GitHub client. Pull requests, issues and code review live in the browser, where they are better. The Octokit integration that once did this was removed in favour of the browser; see `Scans/2026-08-02-github-removal.html`.

**Two products, two release chains:** `master` ships **Grumpy** (no model runtime); `LocalAi` ships
**Grumpy AI (Experimental)** (the same client plus llama.cpp in-process). **The AI edition is
labelled experimental everywhere the user meets it** — window title, exe properties, Start menu,
Apps & features, an installer page they click past, a badge on the review panel and in settings, the
winget package name, the README and every release note. The release job fails if that label goes
missing from the product name, the csproj or the installer. Do not quietly drop it: the review is a
suggestion and says so, and the day it stops being experimental is a decision to raise. They are separate products, not two builds
of one — separate Inno `AppId`, install directory, Start-menu name, setup filename, winget identifier
and tag chain (`v1.2.3` / `ai-v1.2.3`, versioned independently), so installing one never touches the
other and `releases/latest` always means Grumpy. **Plenty of users want a git client and no language
model anywhere near it; that is a supported position, not an oversight.** The release job proves it
rather than asserting it: a standard build whose published output contains any llama/ggml/gguf file
fails, as does an AI build carrying weights or a tag whose prefix disagrees with the tree.

**The build is branch-owned, not parameterised.** `installer/Grumpy.iss`, `.github/workflows/`
and `installer/winget/` each build one product — the branch's own — with no edition switch, no
`/DEdition` flag and no tag-prefix dispatch. Those files conflict on every merge between the two
branches; resolve by keeping the branch's own copy. Do not "fix" that by reintroducing a switch:
one branch, one product, one script was the decision. Product identity lives in `<Product>` /
`<AssemblyTitle>` / `<ApplicationIcon>` in `GrumpyGit.App.csproj` and the window `Icon` in
`MainWindow.axaml`; everything user-visible derives from those, so never write the name again
anywhere else. The AI icon is the sheep with an accent "AI" badge, generated from `sheep.ico` by
`tools/generate-ai-icon.ps1` rather than drawn separately, so the two stay one family — regenerate
it, do not hand-edit it. `%LOCALAPPDATA%\Grumpy` is shared by both on purpose, so review notes
survive a switch.

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

## The Council

Six project-local agents in `.claude/agents/`, each holding one seat. Convene them — in parallel —
for any non-trivial change; they are meant to disagree, and the disagreement is the point.

| Seat | Owns | Voice |
|---|---|---|
| `product-owner` | Whether it should exist at all | Mostly says no |
| `architect` | Where it belongs; the Core/UI boundary and settled decisions | Decides, does not code |
| `staff-engineer` | The hard changes: git backend, lane assignment, diffs, performance | Implements and reviews |
| `mid-level-engineer` | Well-specified work, following existing patterns | Asks rather than invents |
| `ui-designer` | Commit graph, diff viewer, staging flow, theming | Argues for less on screen |
| `security-expert` | Commandments 1, 5 and 9 | **Blocking** — the others defer |

The security expert's verdict is blocking. The rest advise; you decide.

## Scans

`Scans/` is the audit trail, and it is **retained** — reports accumulate, they are never overwritten
or tidied away. A scan is worth having precisely because you can compare it with the one before it.

Write a report after any change that touches the outside world: a git.exe invocation, any other
process launch, anything credential-adjacent, a file written or deleted, a path built from
repository content, or a new package reference. In practice that means the `security-expert` seat produces one whenever
it is convened on such a change, and `network-audit` / `dangerous-code` output is folded into it
rather than left in a terminal scrollback.

- One file per scan, named `Scans/YYYY-MM-DD-<scope>.html` — e.g. `2026-08-02-staging-flow.html`.
- **Self-contained HTML**: inline CSS, no scripts, no external fonts or CDNs.
- Contents: what was scanned (commit or branch), every site found under each of the four headings —
  **network**, **process/executable**, **filesystem**, **dependencies** — each with file, line and a
  one-line verdict, then the overall SHIP / SHIP WITH CHANGES / DO NOT SHIP. For process sites,
  record how arguments reach `git.exe`: as arguments, or built into a string (commandment 5).
- Repo-relative paths only, and no machine names, user paths, tokens, or author names and email
  addresses lifted from a test repository (commandment 9).

`Scans/index.html` is a table of every report, newest first: date, scope, verdict, link. Add the row
in the same change as the report, or the folder becomes a pile no one reads.

## Available Agents

These general-purpose agents live in the global agent store and serve every project. Use them in parallel when working on independent tasks.

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
