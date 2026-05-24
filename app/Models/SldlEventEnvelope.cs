using System.Text.Json;

namespace SldlWeb.Models;

/// <summary>
/// Local event envelope for receiving daemon SignalR events.
/// Uses JsonElement for Payload so individual event handlers can deserialize
/// to the appropriate Sldl.Api event type without the polymorphic "object" problem.
/// </summary>
public sealed record SldlEventEnvelope(
    long Sequence,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string Category,
    bool SnapshotInvalidation,
    Guid? WorkflowId,
    JsonElement Payload);
