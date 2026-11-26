using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using EventStore.PositionRepository.Gprc;
using KurrentDB.Client;
using Linker.Core;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Tests;

/// <summary>
/// Runs against two real KurrentDB nodes. Skipped unless both connection strings are set:
///   LINKER_IT_ORIGIN       e.g. kurrentdb://localhost:21130?tls=false
///   LINKER_IT_DESTINATION  e.g. kurrentdb://localhost:21140?tls=false
/// The destination must run with a small max append size (KURRENTDB_MAX_APPEND_SIZE=65536 and
/// KURRENTDB_MAX_APPEND_EVENT_SIZE=65536)
/// so that one oversized event is rejected there while the origin accepts it.
/// </summary>
[TestFixture]
[Category("Integration")]
public class CheckpointIntegrationTests
{
    private const int OversizedEventBytes = 200 * 1024;

    [Test]
    public async Task Checkpoint_never_passes_an_event_whose_append_failed()
    {
        var originConnectionString = Environment.GetEnvironmentVariable("LINKER_IT_ORIGIN");
        var destinationConnectionString = Environment.GetEnvironmentVariable("LINKER_IT_DESTINATION");
        if (string.IsNullOrEmpty(originConnectionString) || string.IsNullOrEmpty(destinationConnectionString))
            Assert.Ignore("Set LINKER_IT_ORIGIN and LINKER_IT_DESTINATION to run this test.");

        var run = Guid.NewGuid().ToString("N")[..8];
        var adjustedStream = $"it{run}-adjusted";
        var failingStream = $"it{run}-failing";
        var fillerStream = $"it{run}-filler";

        var originBuilder = new LinkerConnectionBuilder(KurrentDBClientSettings.Create(originConnectionString!), $"origin{run}", null);
        var destinationBuilder = new LinkerConnectionBuilder(KurrentDBClientSettings.Create(destinationConnectionString!), $"destination{run}", null);
        await using var origin = originBuilder.Build();
        await using var destination = destinationBuilder.Build();

        // The adjusted stream was already replicated up to event 1 by an earlier run.
        await destination.AppendToStreamAsync(adjustedStream, StreamState.NoStream, [NewEvent(), NewEvent()]);

        // Origin, in $all order: the adjusted stream's history, enough filler that the next two
        // events queue up behind it and are drained in the same cycle, an event the destination
        // rejects, then a new event on the adjusted stream.
        await origin.AppendToStreamAsync(adjustedStream, StreamState.NoStream, [NewEvent(), NewEvent()]);
        await origin.AppendToStreamAsync(fillerStream, StreamState.NoStream, Enumerable.Range(0, 500).Select(_ => NewEvent()));
        var failingWrite = await origin.AppendToStreamAsync(failingStream, StreamState.NoStream, [NewEvent(OversizedEventBytes)]);
        await origin.AppendToStreamAsync(adjustedStream, StreamState.StreamRevision(1), [NewEvent()]);

        var dataFolder = Path.Combine(Path.GetTempPath(), $"linker-it-{run}");
        var name = $"From-{originBuilder.ConnectionName}-To-{destinationBuilder.ConnectionName}";
        Directory.CreateDirectory(Path.Combine(dataFolder, "positions"));
        await File.WriteAllTextAsync(Path.Combine(dataFolder, "positions", $"stream_positions_{name}.json"),
            JsonSerializer.Serialize(new Dictionary<string, ulong> { [adjustedStream] = 1 }));

        var settings = new Settings { DataFolder = dataFolder, BufferSize = 100, EnableReconciliation = true };
        var positionRepository = new PositionRepository($"PositionStream-{destinationBuilder.ConnectionName}", "PositionUpdated", destinationBuilder.Build());
        var sut = new LinkerService(originBuilder, destinationBuilder, positionRepository,
            new FilterService(new Filter(FilterType.Stream, $"it{run}-*", FilterOperation.Include)),
            settings,
            new FileAdjustedStreamRepository(Path.Combine(dataFolder, "positions", $"adjusted_streams_{name}.json"),
                NullLogger<FileAdjustedStreamRepository>.Instance),
            new TestOutputLoggerFactory());

        try
        {
            await sut.StartAsync();

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!sut.RestartRequested && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.That(sut.RestartRequested, Is.True, "the oversized append should have failed the drain cycle");

            // The position repository persists the last position it was given once a second.
            await Task.Delay(TimeSpan.FromSeconds(2.5));

            await sut.StopAsync();

            // The scenario only holds if the adjusted stream's new event was drained in the same
            // cycle as the failing one and written by the event-by-event path.
            ResolvedEvent? lastAdjustedInDestination = null;
            await foreach (var e in destination.ReadStreamAsync(Direction.Backwards, adjustedStream, StreamPosition.End, 1))
                lastAdjustedInDestination = e;
            Assert.That(lastAdjustedInDestination?.OriginalEventNumber.ToUInt64(), Is.EqualTo(2UL));

            // A restart resumes the subscription after this position: it must not have moved past
            // the event that never reached the destination, or that event is skipped for good.
            Assert.That(positionRepository.TryGet(out var checkpoint) ? checkpoint : Position.Start,
                Is.LessThan(failingWrite.LogPosition));
        }
        finally
        {
            await sut.DisposeAsync();
            try { Directory.Delete(dataFolder, recursive: true); } catch (IOException) { }
        }
    }

    private static EventData NewEvent(int dataBytes = 16) =>
        new(Uuid.NewUuid(), "TestEvent", Encoding.UTF8.GetBytes(new string('x', dataBytes)), Encoding.UTF8.GetBytes("{}"));

    private sealed class TestOutputLoggerFactory : ILoggerFactory, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            TestContext.Progress.WriteLine($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}");
        }
    }
}
