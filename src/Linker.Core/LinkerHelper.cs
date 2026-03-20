using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KurrentDB.Client;

namespace Linker.Core;

public class LinkerHelper
{
    public bool IsValidForReplica(string eventType, string eventStreamId, Position? originalPosition,
        string positionEventType, IFilterService? filterService, HashSet<string> streamsToBeExcluded,
        IDictionary<string, JsonNode?>? metadata, string destinationName)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return false;

        if (streamsToBeExcluded.Contains(eventStreamId))
            return false;

        if (eventStreamId.StartsWith("$$$") || (eventStreamId.StartsWith('$') && !eventStreamId.StartsWith("$$")))
            return false;

        if (eventType.Equals(positionEventType) || !originalPosition.HasValue)
            return false;

        if (filterService != null && !filterService.IsValid(eventType, eventStreamId, metadata))
            return false;

        
        if (metadata == null || !metadata.TryGetValue("$origin", out var originNode) || originNode is null) 
            return true;

        // do not replicate events that originated from this destination
        var origins = originNode.ToString().Split(',');
        return origins.All(o => !o.Equals(destinationName, StringComparison.OrdinalIgnoreCase));
    }

    public bool TryProcessMetadata(string streamId, StreamPosition eventNumber, DateTime created, string origin, IDictionary<string, JsonNode?> inputMetadata, out IDictionary<string, JsonNode> outputMetadata)
    {
        outputMetadata = new Dictionary<string, JsonNode>();

        if (inputMetadata.ContainsKey("$local"))
            return false;

        // We don't want to replicate an event back to any of its origins
        if (inputMetadata.TryGetValue("$origin", out var originNode) && originNode != null)
        {
            string[] origins = originNode.ToString().Split(',');
            if (origins.Any(o => o.Equals(origin)))
                return false;
        }

        outputMetadata = EnrichMetadata(streamId, eventNumber.ToInt64(), created, inputMetadata, origin);
        return true;
    }

    private IDictionary<string, JsonNode> EnrichMetadata(string streamId, long eventNumber, DateTime created, IDictionary<string, JsonNode?> metadata, string origin)
    {
        // Convert to mutable dictionary, filtering out null values
        var result = metadata.Where(kvp => kvp.Value != null)
                           .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

        // Handle $origin - either append to existing or add new
        if (result.TryGetValue("$origin", out var existingOrigin))
            result["$origin"] = $"{existingOrigin},{origin}";
        else
            result["$origin"] = origin;

        // Add metadata if not already present
        result.TryAdd("$applies", created.ToString("o"));
        result.TryAdd("$eventStreamId", streamId);
        result.TryAdd("$eventNumber", eventNumber);

        return result;
    }

    public IDictionary<string, JsonNode?> DeserializeObject(ReadOnlyMemory<byte> obj)
    {
        if (obj.IsEmpty)
            return new Dictionary<string, JsonNode?>();

        var json = Encoding.UTF8.GetString(obj.Span);
        var jsonObject = JsonNode.Parse(json)?.AsObject();

        return jsonObject is not null
            ? new Dictionary<string, JsonNode?>(jsonObject)
            : new Dictionary<string, JsonNode?>();
    }

    public byte[] SerializeObject(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return Encoding.UTF8.GetBytes(json);
    }
}