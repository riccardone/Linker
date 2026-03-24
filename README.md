[![Build and Test](https://github.com/riccardone/Linker/actions/workflows/ci.yml/badge.svg)](https://github.com/riccardone/Linker/actions/workflows/ci.yml)

# Linker

Cross-cluster replication tool for [KurrentDb](https://github.com/kurrent-io/KurrentDB) (formerly EventStore).

Linker reads events from an **origin** KurrentDb instance and writes them to a **destination**, preserving ordering and handling conflicts. Multiple replication links can run simultaneously to support advanced topologies such as active-active, fan-out and fan-in.

More background: [Cross Data Center Replication with Linker](http://www.dinuzzo.co.uk/2019/11/17/cross-data-center-replication-with-linker/)

![image](https://github.com/user-attachments/assets/aff928d0-37d3-4624-8021-1e4dde7f62b7)

## Getting started

| Method | Link |
|--------|------|
| Download a release | [Latest release](https://github.com/riccardone/Linker/releases) |
| Docker image | [riccardone/linker](https://hub.docker.com/r/riccardone/linker) |
| NuGet package (programmatic use) | `Linker.Core` on NuGet |

---

## How to configure Linker

Linker can be configured in three ways. Pick whichever suits your deployment model.

### 1. Config file (`appsettings.json`)

The standalone application reads `appsettings.json` (or a custom path set via the `LINKER_CONFIG_PATH` environment variable). Environment-specific overrides such as `appsettings.production.json` are also supported.

#### Global settings

The following properties are set at the root level. Default values are shown:

```jsonc
{
  "DataFolder": "data",             // Folder for stream-position files (must be persistent storage)
  "AutomaticTuning": false,         // Dynamically resize the bounded buffer based on throughput
  "BufferSize": 100,                // Initial bounded-buffer size (clamped to 1-1000)
  "HandleConflicts": true,          // Write conflicts to a special stream instead of failing
  "ResolveLinkTos": false,          // Resolve $> link events in KurrentDb
  "InteractiveMode": false,         // Enable keyboard shortcuts (O/D/E) for manual testing
  "EnableReconciliation": false,    // Verify per-stream write positions on startup
  "Links": []                       // One or more replication links (see below)
}
```

#### Link definition

Each entry in `Links` pairs an **origin** with a **destination** and an optional set of **filters**:

```jsonc
{
  "origin": {
    "connectionString": "esdb://admin:changeit@db01:2113?tls=false",
    "connectionName": "db01",
    "certificate": null,              // Inline PEM certificate (optional)
    "certificatePrivateKey": null,    // Inline PEM private key (optional)
    "certificateFile": null,          // Path to a PEM certificate file (optional)
    "privateKeyFile": null            // Path to a PEM private-key file (optional)
  },
  "destination": {
    "connectionString": "esdb://admin:changeit@db02:2113?tls=false",
    "connectionName": "db02",
    "certificate": null,
    "certificatePrivateKey": null,
    "certificateFile": null,
    "privateKeyFile": null
  },
  "filters": []
}
```

> TLS certificates can be provided either inline (PEM strings) or as file paths. When neither is supplied, Linker connects without client certificates.

### 2. CLI / environment variables

Because the app is built on `Microsoft.Extensions.Configuration`, every setting can also be passed as an environment variable using the standard `__` separator (e.g. `Links__0__Origin__ConnectionString`).

### 3. NuGet package (programmatic)

Reference the `Linker.Core` NuGet package and build a `LinkerService` directly:

```csharp
var origin = new LinkerConnectionBuilder(
    KurrentDBClientSettings.Create("esdb://admin:changeit@localhost:2114?tls=false"),
    "db01", cert: null);

var destination = new LinkerConnectionBuilder(
    KurrentDBClientSettings.Create("esdb://admin:changeit@localhost:2115?tls=false"),
    "db02", cert: null);

var service = new LinkerService(
    origin, destination,
    positionRepository,
    new FilterService(
        new Filter(FilterType.Stream, "domain-*", FilterOperation.Include),
        new Filter(FilterType.EventType, "Basket*", FilterOperation.Exclude)),
    settings,
    adjustedStreamRepository,
    loggerFactory);

await service.StartAsync();
```

---

## Replication topologies

### Active-Passive

One-way replication from origin to destination:

```json
{
  "links": [
    {
      "origin":      { "connectionString": "esdb://admin:changeit@localhost:2114?tls=false", "connectionName": "db01" },
      "destination": { "connectionString": "esdb://admin:changeit@localhost:2115?tls=false", "connectionName": "db02" },
      "filters": [
        { "filterType": "stream", "value": "diary-input", "filterOperation": "exclude" },
        { "filterType": "stream", "value": "*",           "filterOperation": "include" }
      ]
    }
  ]
}
```

### Active-Active

Define two links that mirror each other. Linker uses `$origin` metadata to prevent infinite loops:

```json
{
  "links": [
    {
      "origin":      { "connectionString": "esdb://admin:changeit@localhost:2114?tls=false", "connectionName": "db01" },
      "destination": { "connectionString": "esdb://admin:changeit@localhost:2115?tls=false", "connectionName": "db02" },
      "filters": []
    },
    {
      "origin":      { "connectionString": "esdb://admin:changeit@localhost:2115?tls=false", "connectionName": "db02" },
      "destination": { "connectionString": "esdb://admin:changeit@localhost:2114?tls=false", "connectionName": "db01" },
      "filters": []
    }
  ]
}
```

### Fan-Out

Use the same origin in multiple links, each pointing to a different destination.

### Fan-In

Use different origins in multiple links, all pointing to the same destination.

---

## Filters

Filters control which events are replicated. Without any filters every user event is replicated.

| Filter type | Description | Wildcard support |
|-------------|-------------|------------------|
| `stream` | Match on stream name | `*` (e.g. `domain-*`) |
| `eventType` | Match on event type | `*` (e.g. `User*`) |
| `metadata` | Match on event metadata keys | - |

Each filter specifies an **operation**: `include` or `exclude`.

> **Rule**: if you add an `exclude` filter you must also add at least one `include` filter so Linker knows what else to replicate.

### Examples

**Include only specific streams:**

```csharp
var filter = new Filter(FilterType.Stream, "domain-*", FilterOperation.Include);
```

**Exclude streams by prefix:**

```csharp
var filter = new Filter(FilterType.Stream, "rawdata-*", FilterOperation.Exclude);
```

**Include by event type:**

```csharp
var filter = new Filter(FilterType.EventType, "User*", FilterOperation.Include);
```

**Exclude by event type:**

```csharp
var filter = new Filter(FilterType.EventType, "Basket*", FilterOperation.Exclude);
```

**Combine filters (programmatic):**

```csharp
var service = new LinkerService(origin, destination,
    positionRepository,
    new FilterService(
        new Filter(FilterType.EventType, "User*",    FilterOperation.Include),
        new Filter(FilterType.Stream,    "domain-*", FilterOperation.Include),
        new Filter(FilterType.EventType, "Basket*",  FilterOperation.Exclude)),
    settings,
    adjustedStreamRepository,
    loggerFactory);

await service.StartAsync();
```

**Combine filters (config file):**

```json
"filters": [
  { "filterType": "stream",    "value": "diary-input", "filterOperation": "exclude" },
  { "filterType": "stream",    "value": "*",           "filterOperation": "include" }
]
```

---

## Backpressure and performance

Linker uses a bounded `System.Threading.Channels.Channel` between the subscription reader and the writer. When the buffer is full the reader waits - this is the backpressure mechanism.

- **`AutomaticTuning = true`** - the buffer size is dynamically adjusted based on throughput and latency samples.
- **`AutomaticTuning = false`** - the buffer stays at the configured `BufferSize`. Backpressure still applies but the size never changes.

`BufferSize` is clamped between **1** and **1000**.

---

## Docker

```bash
docker run --rm \
  -v $(pwd)/config:/config \
  -v $(pwd)/certs:/certs \
  -v $(pwd)/data:/data \
  -e LINKER_CONFIG_PATH=/config/appsettings.json \
  riccardone/linker:latest
```

Example `/config/appsettings.json` for Docker:

```json
{
  "DataFolder": "/data",
  "links": [
    {
      "origin": {
        "connectionString": "esdb://admin:changeit@db01:2114?tls=true",
        "connectionName": "db01",
        "certificateFile": "/certs/db01.crt.pem",
        "privateKeyFile": "/certs/db01.key.pem"
      },
      "destination": {
        "connectionString": "esdb://admin:changeit@db02:2115?tls=true",
        "connectionName": "db02",
        "certificateFile": "/certs/db02.crt.pem",
        "privateKeyFile": "/certs/db02.key.pem"
      },
      "filters": []
    }
  ]
}
```

Mount points:

| Path | Purpose |
|------|---------|
| `/config` | Configuration files |
| `/certs` | TLS certificates |
| `/data` | Persistent stream-position storage |
