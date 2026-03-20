using System.Collections.Generic;
using System.Text.Json.Nodes;
using Linker.Core;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Tests;

public class FilterServiceTests
{
    [Test]
    public void can_include_events_using_stream_filters()
    {
        // PREPARE
        var prefix1 = "Stream1Prefix-";
        var prefix2 = "Stream2Prefix-";

        var streamFilter1 = new Filter(FilterType.Stream, $"{prefix1}*", FilterOperation.Include);
        var streamFilter2 = new Filter(FilterType.Stream, $"{prefix2}*", FilterOperation.Include);

        var sut = new FilterService(streamFilter2,streamFilter1);

        // RUN
        var valid1 = sut.IsValid("some type", $"{prefix1}1222");
        var valid2 = sut.IsValid("some type", $"{prefix2}1222");
        var invalid = sut.IsValid("some type", "other stream");

        // ASSERT
        ClassicAssert.True(valid1);
        ClassicAssert.True(valid2);
        ClassicAssert.False(invalid);
    }

    [Test]
    public void can_include_events_using_eventtype_filters()
    {
        // PREPARE
        var prefix1 = "Event1Prefix-";
        var prefix2 = "Event2Prefix-";

        var eventFilter1 = new Filter(FilterType.EventType, $"{prefix1}*", FilterOperation.Include);
        var eventFilter2 = new Filter(FilterType.EventType, $"{prefix2}*", FilterOperation.Include);

        var sut = new FilterService(eventFilter1,eventFilter2);

        // RUN
        var valid1 = sut.IsValid($"{prefix1}1222", "stream1");
        var valid2 = sut.IsValid($"{prefix1}1222", "stream1");
        var invalid = sut.IsValid("otherEvent", "stream1");

        // ASSERT
        ClassicAssert.True(valid1);
        ClassicAssert.True(valid2);
        ClassicAssert.False(invalid);
    }

    [Test]
    public void can_include_events_using_eventtype_and_stream_filters()
    {
        // PREPARE
        var prefix = "someprefix-";

        var streamFilter = new Filter(FilterType.EventType, $"{prefix}*", FilterOperation.Include);
        var eventFilter = new Filter(FilterType.Stream, $"{prefix}*", FilterOperation.Include);

        var sut = new FilterService(streamFilter, eventFilter);

        // RUN
        var valid1 = sut.IsValid($"{prefix}1222", "stream1");
        var valid2 = sut.IsValid("someType", $"{prefix}3322");
        var invalid = sut.IsValid("otherEvent", "otherStream");

        // ASSERT
        ClassicAssert.True(valid1);
        ClassicAssert.True(valid2);
        ClassicAssert.False(invalid);
    }

    [Test]
    public void can_exclude_events_using_stream_filters()
    {
        // PREPARE
        var prefix1 = "Stream1Prefix-";
        var prefix2 = "Stream2Prefix-";

        var streamFilter1 = new Filter(FilterType.Stream, $"{prefix1}*", FilterOperation.Exclude);
        var streamFilter2 = new Filter(FilterType.Stream, $"{prefix2}*", FilterOperation.Exclude);

        var sut = new FilterService(streamFilter2,streamFilter1);

        // RUN
        var invalid1 = sut.IsValid("some type", $"{prefix1}1222");
        var invalid2 = sut.IsValid("some type", $"{prefix2}1222");
        var valid = sut.IsValid("some type", "other stream");

        // ASSERT
        ClassicAssert.False(invalid1);
        ClassicAssert.False(invalid2);
        ClassicAssert.True(valid);
    }

    [Test]
    public void can_exclude_events_using_eventtype_filters()
    {
        // PREPARE
        var prefix1 = "Event1Prefix-";
        var prefix2 = "Event2Prefix-";

        var eventFilter1 = new Filter(FilterType.EventType, $"{prefix1}*", FilterOperation.Exclude);
        var eventFilter2 = new Filter(FilterType.EventType, $"{prefix2}*", FilterOperation.Exclude);

        var sut = new FilterService(eventFilter1,eventFilter2);

        // RUN
        var invalid1 = sut.IsValid($"{prefix1}1222", "stream1");
        var invalid2 = sut.IsValid($"{prefix1}1222", "stream1");
        var valid = sut.IsValid("otherEvent", "stream1");

        // ASSERT
        ClassicAssert.False(invalid1);
        ClassicAssert.False(invalid2);
        ClassicAssert.True(valid);
    }

    [Test]
    public void can_exclude_events_using_eventtype_and_stream_filters()
    {
        // PREPARE
        var prefix = "someprefix-";

        var streamFilter = new Filter(FilterType.EventType, $"{prefix}*", FilterOperation.Exclude);
        var eventFilter = new Filter(FilterType.Stream, $"{prefix}*", FilterOperation.Exclude);

        var sut = new FilterService(streamFilter, eventFilter);

        // RUN
        var invalid1 = sut.IsValid($"{prefix}1222", "stream1");
        var invalid2 = sut.IsValid("someType", $"{prefix}3322");
        var valid = sut.IsValid("otherEvent", "otherStream");

        // ASSERT
        ClassicAssert.False(invalid1);
        ClassicAssert.False(invalid2);
        ClassicAssert.True(valid);
    }

    [Test]
    public void can_filter_using_include_and_exclude_stream_filters()
    {
        // PREPARE
        var prefix = "SomePrefix-";
        var postfix = "SomePostfix-";

        var includeFilter = new Filter(FilterType.Stream, $"{prefix}*", FilterOperation.Include);
        var excludeFilter = new Filter(FilterType.Stream, $"{prefix}-{postfix}*", FilterOperation.Exclude);
            
        var sut = new FilterService(includeFilter, excludeFilter);

        // RUN
        var valid = sut.IsValid("some type", $"{prefix}1222");
        var invalid1 = sut.IsValid("some type", $"{prefix}-{postfix}1222");
        var invalid2 = sut.IsValid("some type", "other stream");

        // ASSERT
        ClassicAssert.False(invalid1);
        ClassicAssert.False(invalid2);
        ClassicAssert.True(valid);
    }

    [Test]
    public void can_filter_using_include_and_exclude_eventtype_filters()
    {
        // PREPARE
        var prefix = "SomePrefix-";
        var postfix = "SomePostfix-";

        var includeFilter = new Filter(FilterType.EventType, $"{prefix}*", FilterOperation.Include);
        var excludeFilter = new Filter(FilterType.EventType, $"{prefix}-{postfix}*", FilterOperation.Exclude);
            
        var sut = new FilterService(includeFilter, excludeFilter);

        // RUN
        var valid = sut.IsValid($"{prefix}1222", "stream");
        var invalid1 = sut.IsValid($"{prefix}-{postfix}1222", "stream");
        var invalid2 = sut.IsValid("some type", "other stream");

        // ASSERT
        ClassicAssert.False(invalid1);
        ClassicAssert.False(invalid2);
        ClassicAssert.True(valid);
    }

    [Test]
    public void can_include_events_using_metadata_filter()
    {
        // PREPARE
        var filter = new Filter(FilterType.Metadata, "tenant-id:123", FilterOperation.Include);
        var sut = new FilterService(filter);

        var matchingMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("123") } };
        var nonMatchingMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("456") } };

        // RUN
        var valid = sut.IsValid("someEvent", "someStream", matchingMetadata);
        var invalid = sut.IsValid("someEvent", "someStream", nonMatchingMetadata);

        // ASSERT
        ClassicAssert.True(valid);
        ClassicAssert.False(invalid);
    }

    [Test]
    public void can_exclude_events_using_metadata_filter()
    {
        // PREPARE
        var filter = new Filter(FilterType.Metadata, "tenant-id:123", FilterOperation.Exclude);
        var sut = new FilterService(filter);

        var matchingMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("123") } };
        var nonMatchingMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("456") } };

        // RUN
        var invalid = sut.IsValid("someEvent", "someStream", matchingMetadata);
        var valid = sut.IsValid("someEvent", "someStream", nonMatchingMetadata);

        // ASSERT
        ClassicAssert.False(invalid);
        ClassicAssert.True(valid);
    }

    [Test]
    public void can_include_events_using_metadata_wildcard_filter()
    {
        // PREPARE
        var filter = new Filter(FilterType.Metadata, "tenant-id:12*", FilterOperation.Include);
        var sut = new FilterService(filter);

        var matchingMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("123") } };
        var alsoMatchingMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("129") } };
        var nonMatchingMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("456") } };

        // RUN
        var valid1 = sut.IsValid("someEvent", "someStream", matchingMetadata);
        var valid2 = sut.IsValid("someEvent", "someStream", alsoMatchingMetadata);
        var invalid = sut.IsValid("someEvent", "someStream", nonMatchingMetadata);

        // ASSERT
        ClassicAssert.True(valid1);
        ClassicAssert.True(valid2);
        ClassicAssert.False(invalid);
    }

    [Test]
    public void metadata_filter_returns_false_when_metadata_is_null()
    {
        // PREPARE
        var filter = new Filter(FilterType.Metadata, "tenant-id:123", FilterOperation.Include);
        var sut = new FilterService(filter);

        // RUN
        var result = sut.IsValid("someEvent", "someStream", null);

        // ASSERT
        ClassicAssert.False(result);
    }

    [Test]
    public void metadata_filter_returns_false_when_key_not_present()
    {
        // PREPARE
        var filter = new Filter(FilterType.Metadata, "tenant-id:123", FilterOperation.Include);
        var sut = new FilterService(filter);

        var metadata = new Dictionary<string, JsonNode?> { { "other-key", JsonValue.Create("123") } };

        // RUN
        var result = sut.IsValid("someEvent", "someStream", metadata);

        // ASSERT
        ClassicAssert.False(result);
    }

    [Test]
    public void can_combine_metadata_and_stream_filters()
    {
        // PREPARE
        var streamFilter = new Filter(FilterType.Stream, "orders-*", FilterOperation.Include);
        var metadataFilter = new Filter(FilterType.Metadata, "tenant-id:123", FilterOperation.Exclude);
        var sut = new FilterService(streamFilter, metadataFilter);

        var tenantMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("123") } };
        var otherTenantMetadata = new Dictionary<string, JsonNode?> { { "tenant-id", JsonValue.Create("456") } };

        // RUN
        var excluded = sut.IsValid("someEvent", "orders-100", tenantMetadata);
        var included = sut.IsValid("someEvent", "orders-100", otherTenantMetadata);
        var notIncluded = sut.IsValid("someEvent", "other-stream", otherTenantMetadata);

        // ASSERT
        ClassicAssert.False(excluded);
        ClassicAssert.True(included);
        ClassicAssert.False(notIncluded);
    }
}