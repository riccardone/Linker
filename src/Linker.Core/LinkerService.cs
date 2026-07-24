using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using System.Timers;
using EventStore.PositionRepository.Gprc;
using KurrentDB.Client;
using Microsoft.Extensions.Logging;
using EventTypeFilter = KurrentDB.Client.EventTypeFilter;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Linker.Core;

public class LinkerService : ILinkerService, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IPositionRepository _positionRepository;
    private readonly IStreamPositionFlusher _flusherForStreamPositions;
    private readonly IFilterService? _filterService;
    private readonly Settings _settings;
    private Position _lastPosition;
    private Position _scanPosition;
    private readonly Lock _positionLock = new();
    private Position _originCurrentEnd = Position.Start;
    private volatile bool _isLive;
    private ulong _previousScanCommit;
    private DateTime _previousScanSampleAt;
    private double _scanRatePerSecond;
    private int _stalledIntervals;
    private int _originEndRefreshCounter;
    public string Name { get; }

    private Position LastPosition
    {
        get { lock (_positionLock) return _lastPosition; }
        set { lock (_positionLock) _lastPosition = value; }
    }

    private Position ScanPosition
    {
        get { lock (_positionLock) return _scanPosition; }
        set { lock (_positionLock) _scanPosition = value; }
    }

    private readonly ILinkerConnectionBuilder _originConnectionBuilder;
    private readonly ILinkerConnectionBuilder _destinationConnectionBuilder;
    private KurrentDBClient? _originConnection;
    private KurrentDBClient? _destinationConnection;

    private Channel<BufferedEvent> _channel;
    private Task? _processingTask;

    private readonly System.Timers.Timer _timerForStats;

    private readonly LinkerHelper _replicaHelper;
    private CancellationTokenSource _cts = new();
    private Task? _subscriptionTask;
    private bool _started;

    private long _replicatedSinceLastStats;
    private long _replicatedTotal;

    private readonly List<long> _latencySamples = [];
    private readonly List<long> _replicationSamples = [];
    private readonly Lock _adaptiveLock = new();
    private int _bufferSize;
    private int _adaptiveIntervalCounter;
    private readonly decimal _allowedIncreaseOrDecreaseAmount = 0.15m;
    private readonly double _significantRegressionToTriggerDecrease = -0.10;
    private readonly double _significantIncreaseToTriggerIncrease = -0.10;
    public const int MaxAllowedBuffer = 1000;
    public const int MinAllowedBuffer = 1;
    private const int StatsIntervalMs = 3000;
    private const int MaxAppendBatchSize = 100;
    private const int MaxAppendBatchBytes = 256 * 1024;
    private const int MaxConcurrentStreamAppends = 8;
    private static readonly TimeSpan ResubscribeDelay = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _appendConcurrency = new(MaxConcurrentStreamAppends, MaxConcurrentStreamAppends);
    private readonly Lock _adjustedLock = new();
    private readonly SemaphoreSlim _adjustedSaveLock = new(1, 1);

    private readonly ConcurrentDictionary<string, SortedDictionary<ulong, BufferedEvent>> _perStreamBuffers = new();
    private readonly ConcurrentDictionary<string, ulong?> _lastWrittenPerStream = new();
    private readonly HashSet<string> _streamsToBeExcluded;
    private readonly HashSet<string> _adjustedStartStreams = new();
    private readonly IAdjustedStreamRepository _adjustedStreamRepository;
    private bool _forceChannelException;
    public bool RestartRequested;

    public LinkerService(ILinkerConnectionBuilder originBuilder, ILinkerConnectionBuilder destinationBuilder,
        IPositionRepository positionRepository, IFilterService? filterService, Settings settings, IAdjustedStreamRepository adjustedStreamRepository,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(LinkerService));
        Name = $"From-{originBuilder.ConnectionName}-To-{destinationBuilder.ConnectionName}";

        _originConnectionBuilder = originBuilder;
        _destinationConnectionBuilder = destinationBuilder;

        _positionRepository = positionRepository;
        _filterService = filterService;
        _settings = settings;
        _bufferSize = Math.Clamp(settings.BufferSize, MinAllowedBuffer, MaxAllowedBuffer);

        var streamPositionsPath = Path.Combine(settings.DataFolder, "positions", $"stream_positions_{Name}.json");
        _flusherForStreamPositions =
            new PeriodicStreamPositionFlusher(streamPositionsPath, loggerFactory.CreateLogger("Flusher"));
        _adjustedStreamRepository = adjustedStreamRepository;
        //_adjustedStreamsPath = Path.Combine(settings.DataFolder, "positions", $"adjusted_streams_{Name}.json");

        _replicaHelper = new LinkerHelper();

        _streamsToBeExcluded =
        [
            $"PositionStream-{_originConnectionBuilder.ConnectionName}",
            $"$$PositionStream-{_originConnectionBuilder.ConnectionName}"
        ];

        _timerForStats = new System.Timers.Timer(StatsIntervalMs);
        _timerForStats.Elapsed += TimerForStats_Elapsed;
    }

    public void ForceChannelExceptionOnce()
    {
        _forceChannelException = true;
    }

    private Channel<BufferedEvent> CreateNewChannel(int size)
    {
        return Channel.CreateBounded<BufferedEvent>(new BoundedChannelOptions(size)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });
    }

    private async Task ResizeChannelAsync(int newSize)
    {
        _logger.LogInformation($"{Name}: Resizing buffer from {_bufferSize} to {newSize}");

        await _cts.CancelAsync();
        _channel.Writer.TryComplete();

        if (_subscriptionTask is not null)
        {
            try { await _subscriptionTask; } catch (OperationCanceledException) { }
        }

        if (_processingTask is not null)
        {
            try { await _processingTask; } catch (OperationCanceledException) { }
        }

        var buffered = new List<BufferedEvent>();
        while (_channel.Reader.TryRead(out var evt))
            buffered.Add(evt);

        _bufferSize = newSize;
        _channel = CreateNewChannel(_bufferSize);

        foreach (var evt in buffered)
            await _channel.Writer.WriteAsync(evt);

        _cts.Dispose();
        _cts = new CancellationTokenSource();

        StartWorkerTasks();
    }

    private void StartWorkerTasks()
    {
        _logger.LogDebug($"{Name}: Starting background tasks...");
        _processingTask = Task.Run(ProcessChannelEventsAsync);
        _subscriptionTask = SubscribeMeGrpc(_cts.Token);
    }

    public async Task<bool> StartAsync()
    {
        if (_started)
            return true;

        // TODO consider remove stop/start and only have one restart method that does stop/start and it's clearer
        await StopAsync(); 

        _cts = new CancellationTokenSource();

        if (!_positionRepository.TryGet(out var storedPosition))
            storedPosition = Position.Start;
        LastPosition = storedPosition;
        ScanPosition = storedPosition;
        _isLive = false;
        _previousScanSampleAt = default;
        _scanRatePerSecond = 0;
        _stalledIntervals = 0;

        var loaded = await _adjustedStreamRepository.LoadAsync();
        lock (_adjustedLock)
        {
            _adjustedStartStreams.Clear();
            foreach (var s in loaded)
                _adjustedStartStreams.Add(s);
        }
        _logger.LogInformation($"{Name}: Loaded {loaded.Count} adjusted start streams from disk");

        _destinationConnection = _destinationConnectionBuilder.Build();
        _originConnection = _originConnectionBuilder.Build();

        await UpdateOriginCurrentEndAsync();
        await _flusherForStreamPositions.StartAsync();

        if (_settings.EnableReconciliation)
            await ReconcileStreamPositions();
        else
            _logger.LogWarning($"{Name}: Reconciliation is disabled — startup will skip verifying stream write positions.");

        _timerForStats.Start();
        _channel = CreateNewChannel(_bufferSize);

        StartWorkerTasks();

        _started = true;
        _logger.LogInformation($"{Name} started.");

        return true;
    }

    public async Task<bool> StopAsync()
    {
        if (!_started)
            return true;

        await _cts.CancelAsync();
        _timerForStats.Stop();

        _channel.Writer.TryComplete();

        if (_subscriptionTask is not null)
        {
            try { await _subscriptionTask; } catch (OperationCanceledException) { }
        }

        if (_processingTask is not null)
        {
            try { await _processingTask; } catch (OperationCanceledException) { }
        }

        _cts.Dispose();
        _cts = null;

        _subscriptionTask = null;
        _processingTask = null;

        await _flusherForStreamPositions.StopAsync();
        await DisposeConnectionsAsync();

        _started = false;
        _logger.LogInformation($"{Name} stopped.");
        return true;
    }

    private async Task ReconcileStreamPositions()
    {
        var streamPositions = await _flusherForStreamPositions.LoadAsync();
        int total = streamPositions.Count;
        int current = 0;

        var stopwatch = Stopwatch.StartNew();
        var lastLogTime = TimeSpan.Zero;

        foreach (var kvp in streamPositions)
        {
            current++;
            var streamId = kvp.Key;
            ulong recoveredPosition;

            try
            {
                var last = await GetLastEventFromAStreamAsync(streamId);
                if (last is { OriginalEvent: not null })
                {
                    recoveredPosition = last.Value.OriginalEventNumber.ToUInt64();
                }
                else
                {
                    recoveredPosition = 0;
                }
            }
            catch (Exception ex)
            {
                recoveredPosition = kvp.Value;

                var last = await GetLastEventFromAStreamAsync($"$${streamId}");
                if (last.HasValue)
                    continue; // stream has been deleted

                _logger.LogWarning(ex, $"{Name}: Could not read {streamId} from destination. Using fallback value {kvp.Value}");
            }

            _lastWrittenPerStream[streamId] = recoveredPosition;
            _flusherForStreamPositions.Update(streamId, recoveredPosition);

            if (recoveredPosition > 0)
                await AddToAdjustedStreams(streamId);

            if (stopwatch.Elapsed - lastLogTime >= TimeSpan.FromSeconds(5))
            {
                lastLogTime = stopwatch.Elapsed;
                _logger.LogInformation($"{Name}: Reconciliation progress {current}/{total} streams ({(current * 100 / total)}%)");
            }
        }

        _logger.LogInformation($"{Name}: ReconcileStreamPositions completed. Total streams reconciliations processed: {total}");
    }

    private async Task DisposeConnectionsAsync()
    {
        if (_originConnection != null)
            await _originConnection.DisposeAsync();
        if (_destinationConnection != null)
            await _destinationConnection.DisposeAsync();
    }

    private async Task UpdateOriginCurrentEndAsync()
    {
        if (_originConnection == null)
            return;

        try
        {
            var result = _originConnection.ReadAllAsync(Direction.Backwards, Position.End, maxCount: 1, resolveLinkTos: false, cancellationToken: _cts.Token);
            await foreach (var evt in result.WithCancellation(_cts.Token))
            {
                if (evt.OriginalPosition.HasValue)
                {
                    _originCurrentEnd = evt.OriginalPosition.Value;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"{Name}: Failed to update origin current end position.");
        }
    }

    private async Task ProcessChannelEventsAsync()
    {
        _logger.LogDebug($"{Name}: Channel processing started");
        try
        {
            var streams = new List<string>();
            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                if (_forceChannelException)
                {
                    _forceChannelException = false;
                    throw new Exception("Simulated test exception in ProcessChannelEventsAsync");
                }

                streams.Clear();
                while (_channel.Reader.TryRead(out var evt))
                {
                    _logger.LogTrace($"{Name}: Dequeued {evt.EventNumber}@{evt.StreamId}");
                    var buffer = _perStreamBuffers.GetOrAdd(evt.StreamId, _ => new SortedDictionary<ulong, BufferedEvent>());
                    buffer[evt.EventNumber.ToUInt64()] = evt;
                    if (!streams.Contains(evt.StreamId))
                        streams.Add(evt.StreamId);
                }

                var appendedPositions = await Task.WhenAll(streams.Select(DrainStreamBufferAsync));

                Position? maxAppended = null;
                foreach (var position in appendedPositions)
                    if (position.HasValue && (maxAppended == null || position.Value > maxAppended.Value))
                        maxAppended = position;

                if (maxAppended.HasValue && maxAppended.Value > LastPosition)
                {
                    LastPosition = maxAppended.Value;
                    _positionRepository.Set(maxAppended.Value);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"{Name}: Channel processing error: {ex.GetBaseException().Message}");
            RestartRequested = true;
        }
    }

    private async Task<Position?> DrainStreamBufferAsync(string streamId)
    {
        if (!_perStreamBuffers.TryGetValue(streamId, out var buffer))
            return null;

        var tracked = _lastWrittenPerStream.TryGetValue(streamId, out var lastWritten);
        var expected = tracked && lastWritten.HasValue ? lastWritten.Value + 1 : 0;

        _logger.LogTrace($"{Name}: LastWritten={(lastWritten.HasValue ? lastWritten.Value.ToString() : "null")}, Expected={expected}, BufferCount={buffer.Count}");
        _logger.LogTrace($"{Name}: Buffer keys: {string.Join(",", buffer.Keys)}");

        foreach (var stale in buffer.Keys.Where(k => k < expected).ToList())
            buffer.Remove(stale);

        if (buffer.Count > 0 && !buffer.ContainsKey(expected))
            _logger.LogDebug($"{Name}: Missing expected event {expected}@{streamId}, can't append");

        var run = new List<BufferedEvent>();
        for (var next = expected; buffer.TryGetValue(next, out var evt); next++)
            run.Add(evt);

        Position? maxAppended = null;
        if (run.Count > 0)
        {
            await _appendConcurrency.WaitAsync(_cts.Token);
            try
            {
                if (tracked && !IsAdjustedStream(streamId))
                    maxAppended = await AppendRunAsync(streamId, run, lastWritten);
                else
                    await AppendRunEventByEventAsync(streamId, run);
            }
            finally
            {
                _appendConcurrency.Release();
            }

            foreach (var evt in run)
                buffer.Remove(evt.EventNumber.ToUInt64());
        }

        if (buffer.Count == 0)
            _perStreamBuffers.TryRemove(streamId, out _);

        return maxAppended;
    }

    private async Task AppendRunEventByEventAsync(string streamId, IEnumerable<BufferedEvent> run)
    {
        foreach (var evt in run)
        {
            await AppendEventAsync(evt);
            var eventNumber = evt.EventNumber.ToUInt64();
            _lastWrittenPerStream[streamId] = eventNumber;
            _flusherForStreamPositions.Update(streamId, eventNumber);
            if (eventNumber == 0)
                await _flusherForStreamPositions.FlushAsync();
        }
    }

    private async Task<Position?> AppendRunAsync(string streamId, List<BufferedEvent> run, ulong? lastWritten)
    {
        if (_destinationConnection == null)
            throw new InvalidOperationException("Destination connection is not initialized");

        Position? maxAppended = null;
        var repaired = false;
        var start = 0;
        while (start < run.Count)
        {
            var chunk = new List<BufferedEvent>();
            long chunkBytes = 0;
            while (start + chunk.Count < run.Count && chunk.Count < MaxAppendBatchSize)
            {
                var evt = run[start + chunk.Count];
                chunkBytes += evt.EventData.Data.Length + evt.EventData.Metadata.Length;
                if (chunk.Count > 0 && chunkBytes > MaxAppendBatchBytes)
                    break;
                chunk.Add(evt);
            }

            var expectedRevision = lastWritten.HasValue
                ? StreamState.StreamRevision(lastWritten.Value)
                : StreamState.NoStream;

            var sw = Stopwatch.StartNew();
            try
            {
                await _destinationConnection.AppendToStreamAsync(streamId, expectedRevision,
                    chunk.Select(e => e.EventData));
            }
            catch (WrongExpectedVersionException)
            {
                if (!repaired)
                {
                    repaired = true;
                    var lastInDest = await GetLastEventFromAStreamAsync(streamId);
                    var actual = lastInDest?.OriginalEventNumber.ToUInt64();
                    _logger.LogWarning($"{Name}: Tracked position for {streamId} was stale (expected last {lastWritten?.ToString() ?? "none"}, destination has {actual?.ToString() ?? "none"}); repaired and retrying");
                    _lastWrittenPerStream[streamId] = actual;
                    if (actual.HasValue)
                        _flusherForStreamPositions.Update(streamId, actual.Value);
                    lastWritten = actual;
                    while (start < run.Count && actual.HasValue && run[start].EventNumber.ToUInt64() <= actual.Value)
                        start++;
                    continue;
                }

                _logger.LogWarning($"{Name}: Batch append conflict on {streamId} persists after repair; retrying event by event");
                await AppendRunEventByEventAsync(streamId, run.Skip(start));
                return maxAppended;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("eventStreamId"))
            {
                _logger.LogWarning(ex, $"{Name}: Batch append rejected for {streamId}; retrying event by event");
                await AppendRunEventByEventAsync(streamId, run.Skip(start));
                return maxAppended;
            }
            sw.Stop();

            lock (_latencySamples)
            {
                _latencySamples.Add(sw.ElapsedMilliseconds);
                if (_latencySamples.Count > 100) _latencySamples.RemoveAt(0);
            }

            var lastEvent = chunk[^1];
            maxAppended = lastEvent.OriginalPosition;

            var lastNumber = lastEvent.EventNumber.ToUInt64();
            _lastWrittenPerStream[streamId] = lastNumber;
            _flusherForStreamPositions.Update(streamId, lastNumber);
            if (!lastWritten.HasValue)
                await _flusherForStreamPositions.FlushAsync();

            Interlocked.Add(ref _replicatedSinceLastStats, chunk.Count);
            Interlocked.Add(ref _replicatedTotal, chunk.Count);

            lastWritten = lastNumber;
            start += chunk.Count;
        }

        return maxAppended;
    }

    private bool IsAdjustedStream(string streamId)
    {
        lock (_adjustedLock)
            return _adjustedStartStreams.Contains(streamId);
    }

    private async Task SaveAdjustedStreamsAsync()
    {
        HashSet<string> snapshot;
        lock (_adjustedLock)
            snapshot = [.._adjustedStartStreams];

        await _adjustedSaveLock.WaitAsync();
        try
        {
            await _adjustedStreamRepository.SaveAsync(snapshot);
        }
        finally
        {
            _adjustedSaveLock.Release();
        }
    }

    private async Task AddToAdjustedStreams(string streamId)
    {
        bool updated;
        lock (_adjustedLock)
        {
            updated = _adjustedStartStreams.Add(streamId) ||
                      !streamId.StartsWith("$$") && _adjustedStartStreams.Add("$$" + streamId);
        }

        if (updated)
            await SaveAdjustedStreamsAsync();
    }

    private async Task SubscribeMeGrpc(CancellationToken ctsToken)
    {
        if (_originConnection == null)
            throw new Exception("Origin connection is not initialized");

        while (!ctsToken.IsCancellationRequested)
        {
            try
            {
                _isLive = false;
                await using var subscription = _originConnection.SubscribeToAll(start: FromAll.After(LastPosition), cancellationToken: ctsToken,
                    resolveLinkTos: _settings.ResolveLinkTos, filterOptions: new SubscriptionFilterOptions(EventTypeFilter.RegularExpression(@"^(\$metadata|[^\$].*)")));
                await foreach (var message in subscription.Messages.WithCancellation(ctsToken))
                {
                    switch (message)
                    {
                        case StreamMessage.Event(var evnt):
                            await HandleEventAsync(evnt);
                            if (evnt.OriginalPosition.HasValue)
                                ScanPosition = evnt.OriginalPosition.Value;
                            break;
                        case StreamMessage.AllStreamCheckpointReached(var checkpoint):
                            ScanPosition = checkpoint;
                            break;
                        case StreamMessage.CaughtUp:
                            _isLive = true;
                            _logger.LogInformation("{Name}: Caught up with origin — subscription is now live", Name);
                            break;
                        case StreamMessage.FellBehind:
                            _isLive = false;
                            _logger.LogInformation("{Name}: Fell behind origin — catching up again", Name);
                            break;
                    }
                }

                _logger.LogWarning("{Name}: Subscription completed unexpectedly at position {Position}; resubscribing in {Delay}s", Name, LastPosition, ResubscribeDelay.TotalSeconds);
                await Task.Delay(ResubscribeDelay, ctsToken);
            }
            catch (OperationCanceledException) when (ctsToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name}: Subscription dropped at position {Position}; resubscribing in {Delay}s", Name, LastPosition, ResubscribeDelay.TotalSeconds);
                try { await Task.Delay(ResubscribeDelay, ctsToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleEventAsync(ResolvedEvent evt)
    {
        if (evt.Event == null || !evt.OriginalPosition.HasValue)
            return;

        var metadata = _replicaHelper.DeserializeObject(evt.Event.Metadata);

        if (!_replicaHelper.IsValidForReplica(evt.Event.EventType, evt.Event.EventStreamId, evt.OriginalPosition.Value,
                _positionRepository.PositionEventType, _filterService, _streamsToBeExcluded, metadata,
                _destinationConnectionBuilder.ConnectionName))
        {
            _logger.LogDebug(
                "{Name}: Skipping event {EventNumber}@{StreamId} - Not valid for replication",
                Name, evt.OriginalEventNumber, evt.OriginalStreamId);
            return;
        }

        if (!_replicaHelper.TryProcessMetadata(evt.Event.EventStreamId, evt.Event.EventNumber, evt.Event.Created,
                _originConnectionBuilder.ConnectionName, metadata, out var enrichedMetadata))
        {
            _logger.LogDebug($"{Name}: Updated global last position to {LastPosition}");
            return;
        }

        var streamId = evt.Event.EventStreamId;
        var eventNumber = evt.Event.EventNumber.ToUInt64();

        await EnsureStreamTrackingAsync(evt, streamId,
            eventNumber);

        if (_lastWrittenPerStream.TryGetValue(streamId, out var lastWritten) && lastWritten.HasValue && eventNumber <= lastWritten.Value)
        {
            _logger.LogDebug("{Name}: Skipping already replicated event {EventNumber}@{StreamId}",
                Name, evt.Event.EventNumber, streamId);
            return;
        }

        var bufferedEvent = new BufferedEvent(
            streamId,
            evt.Event.EventNumber,
            evt.OriginalPosition.Value,
            new EventData(
                evt.Event.EventId,
                evt.Event.EventType,
                evt.Event.Data,
                _replicaHelper.SerializeObject(enrichedMetadata)),
            evt.Event.Created);

        await _channel.Writer.WriteAsync(bufferedEvent, _cts.Token);
        _logger.LogDebug("{Name} Received event {EventNumber}@{StreamId}", Name, bufferedEvent.EventNumber, streamId);
    }

    private async Task EnsureStreamTrackingAsync(ResolvedEvent evt, string streamId, ulong eventNumber)
    {
        if (_lastWrittenPerStream.ContainsKey(streamId))
        {
            _logger.LogDebug("{Name}: Stream {StreamId} already tracked. Skipping initialization.", Name, streamId);
            return;
        }

        if (eventNumber == 0 && !IsAdjustedStream(streamId))
        {
            _lastWrittenPerStream[streamId] = null;
            _logger.LogDebug("{Name}: Tracking new stream {StreamId} optimistically from event 0", Name, streamId);
            return;
        }

        var lastInDest = await GetLastEventFromAStreamAsync(streamId);
        if (lastInDest != null)
        {
            var lastEventNumber = lastInDest.Value.OriginalEventNumber.ToUInt64();
            _lastWrittenPerStream[streamId] = lastEventNumber;
            _flusherForStreamPositions.Update(streamId, lastEventNumber);
            _logger.LogDebug("{Name}: Initialized stream {StreamId} with existing event #{LastEventNumber}",
                Name, streamId, lastEventNumber);
        }
        else if (eventNumber == 0)
        {
            _lastWrittenPerStream[streamId] = null; // Mark as ready but not written yet
            _logger.LogDebug("{Name}: Initialized empty stream {StreamId}, ready to replicate event 0", Name, streamId);
        }
        else if (IsAdjustedStream(streamId))
        {
            _lastWrittenPerStream[streamId] = eventNumber - 1;
            _flusherForStreamPositions.Update(streamId, eventNumber - 1);
            _logger.LogDebug("{Name}: Adjusted start for stream {StreamId}, tracking starts at {EventNumber}",
                Name, streamId, eventNumber - 1);
        }
        else
        {
            _logger.LogTrace("{Name}: Skipping event {EventNumber}@{StreamId} - not initialized and no adjusted start",
                Name, eventNumber, streamId);
        }
    }

    private async Task AppendEventAsync(BufferedEvent evt)
    {
        if (_destinationConnection == null)
            throw new InvalidOperationException("Destination connection is not initialized");

        var sw = Stopwatch.StartNew();
        ulong eventNumber = evt.EventNumber.ToUInt64();
        StreamState expectedRevision;

        try
        {
            var readResult = _destinationConnection.ReadStreamAsync(Direction.Backwards, evt.StreamId, StreamPosition.End, 1);
            ResolvedEvent? lastEvent = null;
            await foreach (var e in readResult)
            {
                lastEvent = e;
                break;
            }

            if (lastEvent is not null)
            {
                var lastEventNumber = lastEvent.Value.OriginalEventNumber.ToUInt64();
                if (eventNumber == lastEventNumber + 1)
                {
                    expectedRevision = StreamState.StreamRevision(lastEventNumber);
                }
                else if (IsAdjustedStream(evt.StreamId))
                {
                    _logger.LogWarning($"{Name}: Skipping gap in adjusted stream {evt.StreamId}. Got {eventNumber}, expected {lastEventNumber + 1}");
                    await UpdateStreamTrackingAsync(evt);
                    return;
                }
                else if (evt.StreamId.StartsWith("$$"))
                {
                    _logger.LogInformation($"{Name}: Skipping out-of-order system event {eventNumber}@{evt.StreamId} (last was {lastEventNumber})");
                    await UpdateStreamTrackingAsync(evt);
                    return;
                }
                else
                {
                    throw new InvalidOperationException($"Out-of-order event detected in {evt.StreamId}. Expected {lastEventNumber + 1}, got {eventNumber}.");
                }
            }
            else if (IsAdjustedStream(evt.StreamId))
            {
                _logger.LogInformation($"{Name}: Stream {evt.StreamId} is empty but adjusted start is enabled — using StreamState.NoStream for event {eventNumber}");
                expectedRevision = StreamState.NoStream;
            }
            else
            {
                if (await IsMaxAgeOrMaxCountStream(evt.StreamId))
                {
                    _logger.LogInformation($"{Name}: Using StreamState.Any for {evt.StreamId} due to $maxCount/$maxAge settings");
                    expectedRevision = StreamState.Any;
                }
                else
                {
                    var isAdjusted = IsAdjustedStream(evt.StreamId);
                    _lastWrittenPerStream.TryGetValue(evt.StreamId, out var lastWritten);

                    _logger.LogWarning($"{Name}: Debug — about to append event {eventNumber}@{evt.StreamId}, adjusted={isAdjusted}, lastWritten={lastWritten}");
                    _logger.LogError($"{Name}: Cannot append event {eventNumber} to empty stream {evt.StreamId}. Adjusted? {isAdjusted}");
                    throw new InvalidOperationException($"Cannot append event {eventNumber} to empty stream.");
                }
            }
        }
        catch (StreamNotFoundException)
        {
            if (eventNumber == 0)
            {
                expectedRevision = StreamState.NoStream;
            }
            else if (IsAdjustedStream(evt.StreamId))
            {
                _logger.LogDebug($"{Name}: Stream {evt.StreamId} missing but start adjusted — using StreamState.NoStream for event {eventNumber}");
                expectedRevision = StreamState.NoStream;
            }
            else if (await IsTombstonedStream(evt.StreamId))
            {
                _logger.LogWarning($"{Name}: Stream {evt.StreamId} is soft deleted. Using NoStream for re-append.");
                await AddToAdjustedStreams(evt.StreamId); 
                expectedRevision = StreamState.NoStream;
            }
            else
            {
                throw new InvalidOperationException($"Event {eventNumber} cannot be appended to missing stream.");
            }
        }
        if (expectedRevision == StreamState.Any)
        {
            _logger.LogError($"{Name}: Refusing to append with {nameof(StreamState.Any)} to stream {evt.StreamId} (event #{evt.EventNumber}) — this is unsafe for replication.");
            throw new InvalidOperationException("Unsafe replication state: StreamState.Any should not be used during replication.");
        }

        try
        {
            await _destinationConnection.AppendToStreamAsync(evt.StreamId, expectedRevision, [evt.EventData]);
            LastPosition = evt.OriginalPosition;
            _positionRepository.Set(evt.OriginalPosition);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("eventStreamId"))
        {
            _logger.LogError(ex, $"{Name}: Skipping event due to invalid stream ID. Stream={evt.StreamId}, EventNumber={evt.EventNumber}");
            await UpdateStreamTrackingAsync(evt);
            return;
        }
        catch (WrongExpectedVersionException ex)
        {
            if (!_settings.HandleConflicts || !await HandleConflictAsync(evt, ex))
            {
                _logger.LogError($"Conflict handling failed: Stream: {evt.StreamId}, EventNumber: {evt.EventNumber}, Error: {ex.Message}");
                throw new InvalidOperationException("Replication halted due to unrecoverable conflict error.");
            }

            await UpdateStreamTrackingAsync(evt);
            return;
        }

        sw.Stop();

        lock (_latencySamples)
        {
            _latencySamples.Add(sw.ElapsedMilliseconds);
            if (_latencySamples.Count > 100) _latencySamples.RemoveAt(0);
        }

        await UpdateStreamTrackingAsync(evt);
    }

    private async Task<bool> IsTombstonedStream(string streamId)
    {
        try
        {
            var original = await _destinationConnection.GetStreamMetadataAsync(streamId);
            // soft deleted
            var deletedStream = await _destinationConnection.GetStreamMetadataAsync($"$${streamId}");
            return true;
        }
        catch (StreamDeletedException) 
        {
            // TODO test if we end up here for hard deleted streams
            // and eventually return handle it differently to keep the hard deleted behaviour in the destination
            return true;
        }
        catch (StreamNotFoundException)
        {
            return false;
        }
    }

    private async Task<bool> IsMaxAgeOrMaxCountStream(string streamId)
    {
        if (_originConnection == null)
            return false;

        try
        {
            var metadataResult = await _originConnection.GetStreamMetadataAsync(streamId, null, null, _cts.Token);
            var metadata = metadataResult.Metadata;
            if (metadata.MaxCount.HasValue || metadata.MaxAge.HasValue)
            {
                return true;
            }
        }
        catch (StreamNotFoundException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"{Name}: Failed to check stream metadata for {streamId}");
        }

        return false;
    }

    private async Task UpdateStreamTrackingAsync(BufferedEvent evt)
    {
        var streamId = evt.StreamId;
        var eventNumber = evt.EventNumber.ToUInt64();

        _lastWrittenPerStream[streamId] = eventNumber;
        _flusherForStreamPositions.Update(streamId, eventNumber);

        Interlocked.Increment(ref _replicatedSinceLastStats);
        Interlocked.Increment(ref _replicatedTotal);

        bool removed;
        lock (_adjustedLock)
            removed = _adjustedStartStreams.Remove(streamId);
        if (removed)
            await SaveAdjustedStreamsAsync();
    }

    private async Task<ResolvedEvent?> GetLastEventFromAStreamAsync(string streamId)
    {
        if (_destinationConnection == null)
            throw new InvalidOperationException("Destination connection not initialised yet.");
        try
        {
            var result = _destinationConnection.ReadStreamAsync(
                Direction.Backwards,
                streamId,
                StreamPosition.End,
                maxCount: 1,
                resolveLinkTos: false,
                cancellationToken: _cts.Token
            );

            await foreach (var evt in result)
                return evt;

            return null;
        }
        catch (StreamNotFoundException)
        {
            _logger.LogDebug($"{Name}: Stream {streamId} not found in destination - assuming it doesn't exist yet.");
            return null;
        }
    }

    private async Task<bool> HandleConflictAsync(BufferedEvent evt, WrongExpectedVersionException ex)
    {
        if (_destinationConnection == null)
            throw new Exception("Destination connection is not initialized");

        var conflictStreamId = _settings.HandleConflicts
            ? $"$conflicts-from-{_originConnectionBuilder.ConnectionName}-to-{_destinationConnectionBuilder.ConnectionName}"
            : evt.StreamId;

        var metadata = _replicaHelper.DeserializeObject(evt.EventData.Metadata);
        metadata["$error"] = ex.GetBaseException().Message;

        try
        {
            await _destinationConnection.AppendToStreamAsync(conflictStreamId, StreamState.Any, new[]
            {
                new EventData(evt.EventData.EventId, evt.EventData.Type, evt.EventData.Data,
                    _replicaHelper.SerializeObject(metadata))
            });
            Interlocked.Increment(ref _replicatedSinceLastStats);
            Interlocked.Increment(ref _replicatedTotal);
            LastPosition = evt.OriginalPosition;
            _positionRepository.Set(evt.OriginalPosition);
            return true;
        }
        catch (Exception innerEx)
        {
            _logger.LogError(innerEx, $"Conflict append failed: {innerEx.Message}");
            return false;
        }
    }

    private void TimerForStats_Elapsed(object? _, ElapsedEventArgs e)
    {
        var replicatedThisInterval = Interlocked.Exchange(ref _replicatedSinceLastStats, 0);
        var totalReplicated = Interlocked.Read(ref _replicatedTotal);
        var bufferSize = _channel.Reader.Count;
        var bufferRatio = (double)bufferSize / _bufferSize;

        long avgLatency;
        lock (_latencySamples)
        {
            avgLatency = _latencySamples.Count > 0 ? (long)_latencySamples.Average() : 0;
        }

        lock (_adaptiveLock)
        {
            _replicationSamples.Add(replicatedThisInterval);
            if (_replicationSamples.Count > 5)
                _replicationSamples.RemoveAt(0);

            _adaptiveIntervalCounter++;

            if (_adaptiveIntervalCounter >= 5)
            {
                _adaptiveIntervalCounter = 0;
                var average = _replicationSamples.Average();
                var previous = _replicationSamples.Take(4).Average();

                if (average == 0 && previous == 0)
                {
                    _logger.LogDebug($"{Name} adaptive tuning skipped: no replication activity. Current BufferSize={_bufferSize}");
                }
                else
                {
                    var percentChange = previous == 0 ? 1 : (average - previous) / previous;
                    var proposed = _bufferSize;

                    if (_settings.AutomaticTuning)
                    {
                        var delta = Math.Max(1, (int)Math.Round(_bufferSize * _allowedIncreaseOrDecreaseAmount));

                        if (percentChange >= _significantIncreaseToTriggerIncrease)
                        {
                            // Steady or improving: increase buffer
                            proposed = Math.Min(MaxAllowedBuffer, _bufferSize + delta);
                        }
                        else if (percentChange < _significantRegressionToTriggerDecrease)
                        {
                            // Significant regression: decrease buffer
                            proposed = Math.Max(MinAllowedBuffer, _bufferSize - delta);
                        }

                        if (proposed != _bufferSize)
                        {
                            _ = ResizeChannelAsync(proposed);
                            _logger.LogInformation($"{Name} adaptive tuning: prevAvg={previous:F1}, currentAvg={average:F1}, proposed bufferSize={proposed}, change={percentChange:P1}");
                        }
                        else
                            _logger.LogDebug($"{Name} adaptive tuning: prevAvg={previous:F1}, currentAvg={average:F1}, proposed bufferSize={proposed}, change={percentChange:P1}");
                    }
                    else
                        _logger.LogDebug($"{Name} prevAvg={previous:F1}, currentAvg={average:F1}, bufferSize={proposed}, change={percentChange:P1}");
                }
            }
        }

        var status = BuildProgressStatus();

        _logger.LogInformation(
            $"{Name} stats: {status}, replicated {replicatedThisInterval} events (total {totalReplicated}), buffer: {bufferSize}/{_bufferSize} ({bufferRatio:P0}), latency: {avgLatency}ms");

        if (++_originEndRefreshCounter >= 10)
        {
            _originEndRefreshCounter = 0;
            _ = UpdateOriginCurrentEndAsync();
        }
    }

    private string BuildProgressStatus()
    {
        if (_isLive)
            return "live";

        var scanCommit = ScanPosition.CommitPosition;
        var originEndCommit = _originCurrentEnd.CommitPosition;

        double scanPercent = 0;
        if (originEndCommit > 0)
            scanPercent = Math.Min(100, (double)scanCommit / originEndCommit * 100);

        var now = DateTime.UtcNow;
        if (_previousScanSampleAt != default && now > _previousScanSampleAt && scanCommit >= _previousScanCommit)
        {
            var rate = (scanCommit - _previousScanCommit) / (now - _previousScanSampleAt).TotalSeconds;
            _scanRatePerSecond = _scanRatePerSecond == 0 ? rate : _scanRatePerSecond * 0.7 + rate * 0.3;
            _stalledIntervals = scanCommit == _previousScanCommit ? _stalledIntervals + 1 : 0;
        }
        _previousScanCommit = scanCommit;
        _previousScanSampleAt = now;

        var status = $"catching up {scanPercent:F1}%";
        if (_stalledIntervals >= 3)
            return status + $", STALLED (no scan progress for {_stalledIntervals * StatsIntervalMs / 1000}s)";

        if (_scanRatePerSecond > 0 && originEndCommit > scanCommit)
        {
            var eta = TimeSpan.FromSeconds((originEndCommit - scanCommit) / _scanRatePerSecond);
            status += eta.TotalHours >= 1
                ? $", ETA {(int)eta.TotalHours}h{eta.Minutes:D2}m"
                : $", ETA {eta.Minutes}m{eta.Seconds:D2}s";
        }

        return status;
    }

    private async Task RestartServiceAsync()
    {
        _logger.LogWarning($"{Name} restarting after error...");
        await Task.Delay(1000);
        await StartAsync();
    }

    public IDictionary<string, dynamic> GetStats() => new Dictionary<string, dynamic>
    {
        ["serviceType"] = "crossReplica",
        ["from"] = _originConnectionBuilder.ConnectionName,
        ["to"] = _destinationConnectionBuilder.ConnectionName,
        ["isRunning"] = _started,
        ["isLive"] = _isLive,
        ["lastPosition"] = LastPosition,
        ["scanPosition"] = ScanPosition,
        ["originEnd"] = _originCurrentEnd,
        ["bufferedEvents"] = _channel.Reader.Count,
        ["replicatedTotal"] = Interlocked.Read(ref _replicatedTotal)
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _timerForStats.Dispose();
    }
}