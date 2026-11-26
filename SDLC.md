# How Linker is built and released

> Three things about Linker shape everything below. It is a public Apache-2.0 repository that outside contributors
> send pull requests to.
> What it ships is versioned (a NuGet package, a Docker image and binaries that other people pin), so a release is a
> tag, not a push. And it has no production of its own, only other people's clusters, so the proof before a push is the
> test suite run against two real KurrentDB nodes. This is the one place the lifecycle is written down;
> `.github/instructions/general.instructions.md` holds the design and code rules. Open work of any kind is an issue on
> this repository, never a file (§1).

## 1. Intake: a request becomes an issue before it becomes code

- Requests arrive as GitHub issues, as pull requests from contributors, or from the owner. Ambiguity is resolved by
  reading the code, the logs a reporter attached and the stored events, and by reproducing the case on the testbed
  (§3), never by guessing. A defect is answered only after its cause is established.
- Every open task, bug and owner decision is an issue on `riccardone/Linker`, checked against the code, git and the
  registries before it is filed or closed. It carries the evidence (file:line, commit, log line), what decides it and a
  "Done when" list. Open work a code comment would name (a `TODO`) is filed as an issue instead; no plan, task list or
  TODO document is kept in the repository, because a file of open work goes stale the day after it is written and then
  reads as true.
- A change that needs design decisions or spans more than one release is an issue with numbered decisions, every
  "today" statement naming the file it was read from, and its slices as sub-issues. The issue IS the plan. When it is
  released the issue closes, and the rules it established move into the instructions file.
- **Labels**: `bug`; `documentation`; `question` when it waits on the owner's decision or act (the title then ends in
  "(owner)"); `good first issue` and `help wanted` for work a contributor can pick up. `enhancement` is not used: it
  says nothing.

## 2. Design rules every change is held to

- **No event is lost or written twice without saying so.** The global checkpoint never moves past an event that is not
  in the destination: it advances only at the end of a drain cycle in which every stream's writes succeeded
  (`LinkerService.ProcessChannelEventsAsync`). A restart re-delivers from the checkpoint, and per-stream tracking skips
  what was already written.
- **Per-stream order is kept; cross-stream order is not promised.** Streams are drained in parallel, so the
  destination's `$all` order can differ from the origin's.
- **A conflict is never skipped silently.** With `HandleConflicts` on it goes to the
  `$conflicts-from-<origin>-to-<destination>` stream. With it off (or when writing to that stream fails), the drain
  fails and logs the conflict; the host restarts the link from the checkpoint, which has not passed the event, so the
  link retries it, about once a second, until the conflict is resolved in the destination.
- **Loop prevention**: every replicated event carries `$origin`; an event whose origin list includes the destination is
  never written back to it.
- **The contract is versioned.** The public API of `Linker.Core` (published as the `Linker.KurrentDB` package) and the
  settings `appsettings.json` accepts change only as semantic versioning allows (§4): within a major version a setting
  is never renamed, removed or given a new default, and a public type or member is never removed.
- **Credentials are never logged**: certificates, private keys and connection strings with passwords.
- **A unit test fakes what `LinkerService` takes through its constructor** (`IFilterService`,
  `IAdjustedStreamRepository`, and `IPositionRepository` from the `EventStore.Tools.PositionRepository.Gprc` package),
  never the KurrentDB client. Behaviour that needs KurrentDB itself is proven by an integration test against real
  nodes (§3).

## 3. Build: tests first

- Every behavioural change comes with a test that fails without it. Unit tests (NUnit, Moq) run everywhere.
  Replication behaviour that needs a server is an integration test in `src/Tests`, `[Category("Integration")]`, that
  calls `Assert.Ignore` unless `LINKER_IT_ORIGIN` and `LINKER_IT_DESTINATION` are set (`CheckpointIntegrationTests` is
  the model). CI has no KurrentDB, so it skips them; the testbed is where they run.
- A rule that changes is written in the instructions file in the same piece of work, stating the current rule rather
  than its history; a change people see gets a sentence in `README.md` (and in `dockerhub-description.md` when it
  concerns the image).
- **The testbed** is two throwaway, in-memory KurrentDB nodes in Docker. The destination has a 64 KiB append limit, so
  a test can make one stream's write fail while the origin accepts the event:

  ```powershell
  docker run -d --rm --name linker-it-origin -p 21130:2113 -e KURRENTDB_INSECURE=true -e KURRENTDB_RUN_PROJECTIONS=None -e KURRENTDB_MEM_DB=true docker.kurrent.io/kurrent-latest/kurrentdb:25.1.0
  docker run -d --rm --name linker-it-destination -p 21140:2113 -e KURRENTDB_INSECURE=true -e KURRENTDB_RUN_PROJECTIONS=None -e KURRENTDB_MEM_DB=true -e KURRENTDB_MAX_APPEND_SIZE=65536 -e KURRENTDB_MAX_APPEND_EVENT_SIZE=65536 docker.kurrent.io/kurrent-latest/kurrentdb:25.1.0
  $env:LINKER_IT_ORIGIN = "kurrentdb://localhost:21130?tls=false"
  $env:LINKER_IT_DESTINATION = "kurrentdb://localhost:21140?tls=false"
  dotnet build src/Linker.App.sln -c Release
  dotnet test src/Linker.App.sln -c Release --no-build
  docker stop linker-it-origin linker-it-destination
  ```

- The gate is that run, with the integration tests enabled: every test passes. A failing count is never reported as
  passing, and a skipped integration test is reported as skipped, not passed. Known trap: in Git Bash, `/p:Name=value`
  arguments are rewritten as paths; prefix the command with `MSYS_NO_PATHCONV=1` or use PowerShell.

## 4. Ship: review, prove, push to `main`, release with a tag

- **The owner's work is made on `main`.** No feature branches and no pull requests for it: one commit per piece of
  work, the subject saying what changed and why, a body that says `Fixes #<n>` when it finishes an issue, ending with
  the Claude Code attribution line. `main` is protected against deletion and force-pushes (the ruleset on `main`).
- **Before the push, two gates, in this order; nothing is pushed while either is open.**
  1. **Review**: `/workspace-review C:\code\opensource\Linker`, the review of everything `main` holds that origin does
     not and of every uncommitted change. A reviewer that has not seen the conversation (the skill pins its model
     and effort) reads the change against section 2 and the instructions file. The author's session verifies each
     finding and either fixes it on `main` with a test or answers why it stands (on the issue, when there is one);
     nothing is dropped silently. Work changed after the review is reviewed again, for what is new.
  2. **The testbed** (§3): the whole suite green with the integration tests enabled, against the build being pushed.
- Then `git push origin main`. CI (`.github/workflows/ci.yml`) builds and tests on Linux; a red `main` is fixed before
  anything else is pushed or released.
- **Contributors' pull requests** come from forks. They get the same two gates before merging: the review run on the
  pull request's branch, with the findings posted on the pull request, and the testbed run on its head. CI must be
  green. They are squash-merged, so the subject ends in `(#<n>)`.
- **A release is a tag**, cut when `main` holds what should ship and CI is green on that commit:
  - The version follows semantic versioning against the §2 contract: patch for fixes, minor for features and
    compatible changes of behaviour, major for a breaking change to the public API or the settings.
  - `git tag vX.Y.Z <sha>` then `git push origin refs/tags/vX.Y.Z`. `tag-and-push.sh` does the same for the commit
    checked out (it prompts for the version and tags `HEAD`), and checks neither the branch nor CI.
  - The tag runs `.github/workflows/release.yml`: build, pack `Linker.KurrentDB` with the tag's version, NuGet login
    through trusted publishing (no stored key), push to NuGet.org and GitHub Packages, self-contained zips for
    linux-x64, win-x64 and osx-arm64, the Docker image `riccardone/linker:<version>` and `latest`, and the GitHub release with the
    commits since the previous tag.
- **Tags are never moved or deleted once pushed.** A broken release is fixed by the next patch version.

## 5. Verify the release, then close the loop

- A green release run is not the proof; every artifact is checked:
  - the NuGet push log says "Your package was pushed", and nuget.org lists the version (a new package id is validated
    for some minutes first);
  - the Docker Hub image has the version's tag;
  - the GitHub release has the three zips and a changelog that lists every commit since the previous tag.
- Issues the release closes are answered with the version that carries the fix, and the reporter is told what to do
  differently, if anything.
- NuGet publishing depends on the trusted publishing policy on nuget.org (owner `riccardone`, repository
  `riccardone/Linker`, workflow `release.yml`, packages `Linker.*`). Renaming the workflow or the package outside that
  pattern changes the policy in the same piece of work.

## 6. What we do not do

- No force-pushes, no moved or deleted tags, no skipped hooks, no push with an unfixed review finding or a red test, no
  release from a red `main`.
- No feature branches or pull requests for the owner's own work: pull requests are how contributors' work arrives.
- No long-lived publishing keys: NuGet publishes through trusted publishing. The only secrets the release uses are the
  Docker Hub credentials and the `GITHUB_TOKEN` GitHub provides for each run; a secret nothing uses is deleted.
- No staging environment: the testbed is local.
- No open work kept as a document: a plan, a task list or a TODO is an issue (§1).

## The repository this applies to

`riccardone/Linker`: `src/Linker` (the host: the Docker image and the zips), `src/Linker.Core` (the library, published
to NuGet as `Linker.KurrentDB`) and `src/Tests`.
