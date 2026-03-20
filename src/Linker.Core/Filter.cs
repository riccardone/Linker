namespace Linker.Core;

public enum FilterOperation
{
    Include,
    Exclude
}

public enum FilterType
{
    Stream,
    EventType,
    Metadata
}

public class Filter
{
    public FilterType FilterType { get; set; }
    public string Value { get; set; }
    public FilterOperation FilterOperation { get; set; }

    public Filter(FilterType filterType, string value, FilterOperation filterOperation)
    {
        FilterType = filterType;
        Value = value;
        FilterOperation = filterOperation;
    }
}