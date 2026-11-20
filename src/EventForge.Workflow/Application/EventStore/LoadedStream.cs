namespace EventForge.Workflow.Application.EventStore;

/// <summary>The full ordered history of one stream. <see cref="Version"/> is 0 for a stream with no events.</summary>
public sealed record LoadedStream(StreamKey Stream, int Version, IReadOnlyList<StoredEvent> Events);
