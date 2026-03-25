# Linker - General Instructions

## Project overview

Linker is a cross-cluster replication tool for **KurrentDb** (formerly EventStore). It reads events from an origin KurrentDb instance and writes them to a destination, preserving per-stream ordering, handling write conflicts, and preventing infinite replication loops via `$origin` metadata.

Repository: <https://github.com/riccardone/Linker>

---

## Solution structure

The solution lives under `src/` and contains three projects:

| Project | Type | Description |
|---------|------|-------------|
| `Linker` | Console app (exe) | Host process. Uses `Microsoft.Extensions.Hosting` for configuration, logging and DI. Entry point is `Program.cs` → `ReplicaApp.RunAsync()`. |
| `Linker.Core` | Class library (NuGet package) | All replication logic. Published as a NuGet package so consumers can embed Linker programmatically. |
| `Tests` | Test project (NUnit + Moq) | Unit tests for filter logic and linker service behaviour. |

All projects target **.NET 10** (`net10.0`).

---

## Key classes and responsibilities

### Linker (host app)

| Class | File | Role |
|-------|------|------|
| `Program` | `Linker/Program.cs` | Builds the generic host, binds `Settings`, registers services, runs `ReplicaApp`. |
| `ReplicaApp` | `Linker/ReplicaApp.cs` | Reads `Settings.Links`, builds a `LinkerService` per link, starts them, monitors for restart requests, and optionally enables interactive keyboard mode. |
| `CertManager` | `Linker/CertManager.cs` | Resolves TLS client certificates from inline PEM strings or file paths using BouncyCastle. |

### Linker.Core (library)

| Class / Interface | File | Role |
|-------------------|------|------|
| `LinkerService` | `LinkerService.cs` | Primary replication engine. Subscribes to the origin `$all` stream, buffers events in a bounded `Channel<BufferedEvent>`, writes to destination with per-stream ordering, tracks global and per-stream positions, supports adaptive buffer resizing, reconciliation, conflict handling, and auto-restart on channel errors. |
| `LinkerServiceSimplified` | `LinkerServiceSimplified.cs` | Simplified variant of the replication engine (same interface). |
| `ILinkerService` | `ILinkerService.cs` | Interface: `Name`, `StartAsync()`, `StopAsync()`, `GetStats()`. |
| `LinkerConnectionBuilder` | `LinkerConnectionBuilder.cs` | Builds a `KurrentDBClient` from `KurrentDBClientSettings`, connection name, and optional certificate. |
| `ILinkerConnectionBuilder` | `ILinkerConnectionBuilder.cs` | Interface: `ConnectionName`, `Build()`. |
| `Settings` | `Settings.cs` | Root configuration model bound from `appsettings.json`. Contains `DataFolder`, `AutomaticTuning`, `BufferSize`, `HandleConflicts`, `ResolveLinkTos`, `InteractiveMode`, `EnableReconciliation`, and `Links`. |
| `Link` | `Settings.cs` | Configuration for a single replication pair: `Origin`, `Destination`, `Filters`. |
| `Origin` / `Destination` | `Settings.cs` | Connection details: `ConnectionString`, `ConnectionName`, optional certificate fields (`Certificate`, `CertificatePrivateKey`, `CertificateFile`, `PrivateKeyFile`). |
| `Filter` | `Filter.cs` | Model: `FilterType` (Stream / EventType / Metadata), `Value` (supports `*` wildcard), `FilterOperation` (Include / Exclude). |
| `FilterService` | `FilterService.cs` | Evaluates a collection of filters against an event's type, stream id, and metadata. Constructor accepts `params Filter[]` or `IEnumerable<Filter>`. |
| `IFilterService` | `IFilterService.cs` | Interface: `IsValid(eventType, streamId)` and `IsValid(eventType, streamId, metadata)`. |
| `LinkerHelper` | `LinkerHelper.cs` | Stateless helpers — `IsValidForReplica()` checks system-stream exclusions, `$origin` loop detection, and filter evaluation. `TryProcessMetadata()` enriches metadata with origin/timestamp/position tracking. |
| `BufferedEvent` | `BufferedEvent.cs` | Immutable wrapper around an event flowing through the channel: `StreamId`, `EventNumber`, `OriginalPosition`, `EventData`, `Created`. |
| `PeriodicStreamPositionFlusher` | `PeriodicStreamPositionFlusher.cs` | Periodically flushes per-stream write positions to a JSON file on disk. Thread-safe via `ConcurrentDictionary` and `SemaphoreSlim`. |
| `IStreamPositionFlusher` | `IStreamPositionFlusher.cs` | Interface for the flusher. |
| `FileAdjustedStreamRepository` | `FileAdjustedStreamRepository.cs` | Persists a `HashSet<string>` of adjusted stream names to disk (one stream per line). |
| `IAdjustedStreamRepository` | `IAdjustedStreamRepository.cs` | Interface: `LoadAsync()`, `SaveAsync()`. |
| `LinkerPosition` | `LinkerPosition.cs` | Value type wrapping commit/prepare positions. |
| `LinkerFromAll` | `LinkerFromAll.cs` | Struct representing a position in the `$all` stream (Start / End). |
| `LinkerCatchUpSubscription` | `LinkerCatchUpSubscription.cs` | Placeholder / stub class. |
| `Ensure` | `Ensure.cs` | Guard-clause helper. |

---

## Configuration

Configuration is loaded via `Microsoft.Extensions.Configuration` in the following order (last wins):

1. `appsettings.json` (or custom path from `LINKER_CONFIG_PATH` env var)
2. `appsettings.{ASPNETCORE_ENVIRONMENT}.json`
3. Environment variables (using `__` as section separator)

The root object is bound to `Settings`. Each `Link` in `Settings.Links` defines one replication pair.

### Settings defaults (from code)

| Property | Default | Notes |
|----------|---------|-------|
| `DataFolder` | `"/data"` | Changed to `"data"` in `appsettings.json` |
| `AutomaticTuning` | `false` | |
| `BufferSize` | `100` | Clamped at startup to `1`–`1000` |
| `HandleConflicts` | `true` | |
| `ResolveLinkTos` | `false` | |
| `InteractiveMode` | `false` | |
| `EnableReconciliation` | `true` | Changed to `false` in `appsettings.json` |
| `Links` | empty list | |

### TLS certificates

Each `Origin` / `Destination` supports four optional certificate fields:
- `Certificate` + `CertificatePrivateKey` — inline PEM strings
- `CertificateFile` + `PrivateKeyFile` — paths to PEM files on disk

`CertManager` tries inline first, then files. If none are provided the connection is made without client certificates.

---

## Replication architecture

1. **Subscription** — `LinkerService` subscribes to the origin's `$all` stream from the last saved global position.
2. **Filtering** — each event is checked against `LinkerHelper.IsValidForReplica()` which excludes system streams, position-tracking events, events that originated from the destination (`$origin` metadata), and events rejected by `FilterService`.
3. **Buffering** — valid events are written into a bounded `Channel<BufferedEvent>`. When the channel is full the subscription pauses (backpressure).
4. **Writing** — a consumer task reads from the channel and appends events to the destination, maintaining per-stream ordering via `_perStreamBuffers` and `_lastWrittenPerStream`.
5. **Position tracking** — the global position is saved to a KurrentDb stream (via `IPositionRepository`). Per-stream positions are flushed periodically to a JSON file on disk (via `PeriodicStreamPositionFlusher`).
6. **Conflict handling** — when `HandleConflicts` is enabled, write conflicts are appended to a dedicated conflict stream rather than failing the replication.
7. **Adaptive tuning** — when `AutomaticTuning` is enabled, the buffer size is resized periodically based on throughput and latency samples.
8. **Reconciliation** — when `EnableReconciliation` is enabled, on startup `LinkerService` verifies that every per-stream position on disk matches the actual last event in the destination stream and corrects mismatches.
9. **Auto-restart** — `ReplicaApp` polls `LinkerService.RestartRequested` and performs stop/start if the channel encountered an error.

### Loop prevention (active-active)

`LinkerHelper.TryProcessMetadata()` stamps each replicated event with `$origin` metadata containing the origin's connection name. On the receiving side, `IsValidForReplica()` checks `$origin` and drops events whose origin list includes the current destination.

---

## Filters

Filters are optional. Without filters all user events are replicated. Three filter types exist:

| `FilterType` | Matches on | `Value` format |
|--------------|------------|----------------|
| `Stream` | `eventStreamId` | Stream name or prefix with `*` wildcard (e.g. `domain-*`). |
| `EventType` | `eventType` | Event type name or prefix with `*` wildcard (e.g. `User*`). |
| `Metadata` | Event metadata | `key:value` — the key is looked up in the event's JSON metadata and the value is compared. The value side supports `*` wildcard (e.g. `tenant-id:abc123` or `tenant-id:abc*`). Implemented in `FilterService.IsMetadataMatch()`. |

Each filter has a `FilterOperation` (`Include` or `Exclude`).

**Important rule**: if you add an `Exclude` filter you must also add at least one `Include` filter.

`FilterService` constructor accepts `params Filter[]` or `IEnumerable<Filter>`.

---

## Replication topologies

| Topology | How to configure |
|----------|-----------------|
| **Active-Passive** | Single link from origin to destination. |
| **Active-Active** | Two links mirroring each other (swap origin/destination). Loop prevention via `$origin` metadata. |
| **Fan-Out** | Same origin in multiple links, each with a different destination. |
| **Fan-In** | Different origins in multiple links, all with the same destination. |

---

## Docker

The Dockerfile is at `src/Linker/Dockerfile`. It uses a multi-stage build (SDK → runtime). The final image expects three mount points:

| Mount | Purpose |
|-------|---------|
| `/config` | `appsettings.json` (path set via `LINKER_CONFIG_PATH` env var) |
| `/certs` | PEM certificate and key files |
| `/data` | Persistent stream-position storage |

---

## Testing

- Framework: **NUnit 4** with **Moq**.
- Test project: `Tests/Tests.csproj`, references `Linker.Core`.
- Key test classes: `FilterServiceTests` (filter logic), `LinkerServiceTests` (service behaviour).

---

## Dependencies

| Package | Used in | Purpose |
|---------|---------|---------|
| `EventStore.Tools.PositionRepository.Gprc` | Linker.Core | Stores/retrieves global replication position in a KurrentDb stream. |
| `KurrentDB.Client` (transitive) | Linker.Core | gRPC client for KurrentDb. |
| `BouncyCastle.NetCore` | Linker | PEM certificate parsing. |
| `Microsoft.Extensions.Hosting` | Linker | Generic host, DI, configuration, logging. |
| `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` | Linker | Docker tooling in Visual Studio. |
| `NUnit` / `NUnit3TestAdapter` / `Microsoft.NET.Test.Sdk` | Tests | Test framework and runner. |
| `Moq` | Tests | Mocking library. |

---

## Coding conventions

- Target framework: `net10.0`. Implicit usings and nullable reference types are enabled.
- Primary constructors are used where appropriate (e.g. `LinkerConnectionBuilder`, `CertManager`, `FileAdjustedStreamRepository`).
- Async methods follow the `*Async` suffix convention.
- Logging uses `Microsoft.Extensions.Logging` with structured log messages.
- The project uses `System.Threading.Channels` for the producer-consumer pipeline and `System.Timers.Timer` for periodic work.
- Configuration binding uses `Microsoft.Extensions.Configuration.Binder` (call to `context.Configuration.Bind(settings)`).
- Tests use the Arrange-Act-Assert pattern with `ClassicAssert` from NUnit.
