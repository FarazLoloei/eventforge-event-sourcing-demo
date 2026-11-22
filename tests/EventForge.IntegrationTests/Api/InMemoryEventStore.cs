using System.Text.Json;
using EventForge.Workflow.Application.EventStore;

namespace EventForge.IntegrationTests.Api;

/// <summary>
/// A scripted in-memory <see cref="IEventStore"/> for API tests. It models the expected-version check so the command
/// flow can be exercised over HTTP, but it is not evidence of PostgreSQL behavior (the PostgreSQL tests cover that).
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly Dictionary<StreamKey, List<StoredEvent>> _streams = [];

    public int LoadCalls { get; private set; }

    public int AppendCalls { get; private set; }

    public IReadOnlyList<NewEvent> LastAppended { get; private set; } = [];

    public Exception? LoadException { get; set; }

    public Exception? AppendException { get; set; }

    public AppendResult? ForcedAppendResult { get; set; }

    public IReadOnlyList<StoredEvent> EventsOf(Guid taskId) =>
        _streams.GetValueOrDefault(new StreamKey("Task", taskId)) ?? [];

    public void Seed(Guid taskId, params StoredEvent[] events) =>
        _streams[new StreamKey("Task", taskId)] = [.. events];

    public Task<LoadedStream> LoadStreamAsync(StreamKey stream, CancellationToken cancellationToken)
    {
        LoadCalls++;
        if (LoadException is not null)
        {
            throw LoadException;
        }

        var events = _streams.GetValueOrDefault(stream) ?? [];
        return Task.FromResult(new LoadedStream(stream, events.Count, events.ToList()));
    }

    public Task<AppendResult> AppendAsync(
        StreamKey stream, int expectedVersion, IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
    {
        AppendCalls++;
        LastAppended = events;
        if (AppendException is not null)
        {
            throw AppendException;
        }

        if (ForcedAppendResult is not null)
        {
            return Task.FromResult(ForcedAppendResult);
        }

        var existing = _streams.GetValueOrDefault(stream) ?? [];
        if (existing.Count != expectedVersion)
        {
            return Task.FromResult(AppendResult.VersionConflict(existing.Count));
        }

        var stored = events
            .Select((e, i) => new StoredEvent(
                e.EventId, stream.StreamType, stream.StreamId, expectedVersion + i + 1,
                e.EventType, e.SchemaVersion, e.Payload, e.Metadata, DateTimeOffset.UtcNow))
            .ToList();
        _streams[stream] = [.. existing, .. stored];
        return Task.FromResult(AppendResult.Appended(expectedVersion + events.Count, stored));
    }

    public static StoredEvent Stored(
        Guid taskId, int version, string type, int schemaVersion, string payloadJson, DateTimeOffset? recordedAt = null,
        string metadataJson = "{}", Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), "Task", taskId, version, type, schemaVersion,
            JsonDocument.Parse(payloadJson).RootElement.Clone(), JsonDocument.Parse(metadataJson).RootElement.Clone(),
            recordedAt ?? DateTimeOffset.UtcNow);
}
