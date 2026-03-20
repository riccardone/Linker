using System.Text.Json.Nodes;

namespace Linker.Core;

public class FilterService : IFilterService
{
    private readonly IDictionary<FilterOperation, List<Filter>> _filters;

    public FilterService(params Filter[] filters) :this(filters.AsEnumerable()){}

    public FilterService(IEnumerable<Filter> filters)
    {
        _filters = new Dictionary<FilterOperation, List<Filter>>();
        if (filters == null)
            return;
        foreach (var replicaFilter in filters)
        {
            if (_filters.ContainsKey(replicaFilter.FilterOperation))
            {
                _filters[replicaFilter.FilterOperation].Add(replicaFilter);
            }
            else
            {
                _filters.Add(replicaFilter.FilterOperation, new List<Filter> { replicaFilter });
            }
        }
    }

    public bool IsValid(string eventType, string eventStreamId)
    {
        return IsValid(eventType, eventStreamId, null);
    }

    public bool IsValid(string eventType, string eventStreamId, IDictionary<string, JsonNode?>? metadata)
    {
        if (!_filters.Any())
            return true;

        var shouldBeExcluded = _filters.ContainsKey(FilterOperation.Exclude) && IsExcludedByFilters(eventType, eventStreamId, metadata, _filters[FilterOperation.Exclude]);
        var shouldBeIncluded = !_filters.ContainsKey(FilterOperation.Include) || IsIncludedByFilters(eventType, eventStreamId, metadata, _filters[FilterOperation.Include]);

        return !shouldBeExcluded && shouldBeIncluded;
    }

    private static bool IsIncludedByFilters(string eventType, string eventStreamId, IDictionary<string, JsonNode?>? metadata, List<Filter> filters)
    {
        if (filters == null || !filters.Any())
            return true;

        var shouldIncludeByStream = filters.Any(f => f.FilterType == FilterType.Stream && IsMatch(eventStreamId, f.Value));
        var shouldIncludeByEventType = filters.Any(f => f.FilterType == FilterType.EventType && IsMatch(eventType, f.Value));
        var shouldIncludeByMetadata = filters.Any(f => f.FilterType == FilterType.Metadata && IsMetadataMatch(metadata, f.Value));

        return shouldIncludeByEventType || shouldIncludeByStream || shouldIncludeByMetadata;
    }

    private static bool IsExcludedByFilters(string eventType, string eventStreamId, IDictionary<string, JsonNode?>? metadata, List<Filter> filters)
    {
        if (filters == null || !filters.Any())
            return false;

        var shouldExcludeByStream = filters.Any(f => f.FilterType == FilterType.Stream && IsMatch(eventStreamId, f.Value));
        var shouldExcludeByEventType = filters.Any(f => f.FilterType == FilterType.EventType && IsMatch(eventType, f.Value));
        var shouldExcludeByMetadata = filters.Any(f => f.FilterType == FilterType.Metadata && IsMetadataMatch(metadata, f.Value));

        return shouldExcludeByStream || shouldExcludeByEventType || shouldExcludeByMetadata;
    }

    private static bool IsMatch(string actual, string pattern)
    {
        if (pattern.EndsWith("*"))
            return actual.StartsWith(pattern.TrimEnd('*'));

        return actual.Equals(pattern);
    }

    private static bool IsMetadataMatch(IDictionary<string, JsonNode?>? metadata, string filterValue)
    {
        if (metadata == null)
            return false;

        var separatorIndex = filterValue.IndexOf(':');
        if (separatorIndex < 0)
            return false;

        var key = filterValue[..separatorIndex];
        var valuePattern = filterValue[(separatorIndex + 1)..];

        if (!metadata.TryGetValue(key, out var node) || node is null)
            return false;

        var actual = node.ToString();
        return IsMatch(actual, valuePattern);
    }
}