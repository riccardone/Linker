**Linker** – Cross-cluster replication for [KurrentDb](https://github.com/kurrent-io/KurrentDB) (formerly EventStore).

Replicates events from an origin KurrentDb instance to a destination, preserving ordering and handling conflicts. Supports active-passive, active-active, fan-out and fan-in topologies.

## Quick start

```
docker run --rm \
  -v $(pwd)/config:/config \
  -v $(pwd)/certs:/certs \
  -v $(pwd)/data:/data \
  -e LINKER_CONFIG_PATH=/config/appsettings.json \
  riccardone/linker:latest
```

## Mount points

| Path | Purpose |
|------|---------|
| `/config` | Configuration files (`appsettings.json`) |
| `/certs` | TLS client certificates (PEM) |
| `/data` | Persistent stream-position storage |

## Minimal configuration

Create `/config/appsettings.json`:

```
{
  "DataFolder": "/data",
  "links": [
    {
      "origin": {
        "connectionString": "esdb://admin:changeit@db01:2113?tls=false",
        "connectionName": "db01"
      },
      "destination": {
        "connectionString": "esdb://admin:changeit@db02:2113?tls=false",
        "connectionName": "db02"
      },
      "filters": []
    }
  ]
}
```

For TLS connections add `certificateFile` and `privateKeyFile` pointing to PEM files in `/certs`.

## Key settings

| Setting | Default | Description |
|---------|---------|-------------|
| `DataFolder` | `/data` | Persistent storage for stream positions |
| `AutomaticTuning` | `false` | Dynamically resize the internal buffer |
| `BufferSize` | `100` | Bounded-buffer size (1–1000) |
| `HandleConflicts` | `true` | Write conflicts to a special stream |
| `EnableReconciliation` | `false` | Verify stream positions on startup |

## Topologies

- **Active-Passive** – single link, one-way replication
- **Active-Active** – two mirrored links (loop prevention via `$origin` metadata)
- **Fan-Out** – same origin, multiple destinations
- **Fan-In** – multiple origins, same destination

## Links

- **GitHub**: https://github.com/riccardone/Linker
- **NuGet** (programmatic use): `Linker.Core`
