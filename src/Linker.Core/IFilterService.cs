using System.Text.Json.Nodes;

namespace Linker.Core;

public interface IFilterService
{
    bool IsValid(string eventType, string eventStreamId);
    bool IsValid(string eventType, string eventStreamId, IDictionary<string, JsonNode?>? metadata);
}