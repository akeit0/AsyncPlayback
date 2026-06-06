namespace AsyncPlayback;

public readonly record struct TimelineRecordInfo(
    RecordId Id,
    string TypeId,
    string TypeName,
    TimeSpan StartTime,
    TimeSpan Duration,
    string DebugLabel,
    RecordId? ParentId,
    int Depth,
    TimelineRecordVisibility Visibility,
    CheckpointRecordKind? CheckpointKind,
    long? Timestamp,
    TimeSpan? DeltaTime
)
{
    public TimeSpan EndTime => StartTime + Duration;

    public bool IsCheckpoint(CheckpointRecordKind kind)
    {
        return TypeId == TimelineRecordTypes.Checkpoint.Id && CheckpointKind == kind;
    }

    public override string ToString()
    {
        var indent = new string(' ', Depth * 2);
        var parent = ParentId is { } id ? $" parent=#{id}" : "";
        return $"{indent}#{Id} {TypeName} {StartTime} - {EndTime}{parent}: {DebugLabel}";
    }
}
