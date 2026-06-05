using System.Globalization;
using System.Text.Json;

namespace AsyncPlayback;

public sealed record TimelineExportOptions
{
    public TimeSpan? SampleInterval { get; init; }
    public bool IncludePointsInSamples { get; init; } = true;
    public bool IncludeInfrastructureRecords { get; init; } = true;
}

public sealed record PlaybackTimelineExport(
    string Schema,
    string GeneratedAt,
    TimelinePlaybackExport Playback,
    IReadOnlyList<TimelineRecordExport> Records,
    IReadOnlyList<TimelineSampleExport> Samples
);

public sealed record TimelinePlaybackExport(
    string Time,
    long TimeTicks,
    double TimeSeconds,
    string TargetTime,
    long TargetTimeTicks,
    double TargetTimeSeconds,
    string Direction,
    string Mode,
    bool IsCompleted,
    long Timestamp,
    string DeltaTime,
    long DeltaTimeTicks,
    double DeltaTimeSeconds
);

public sealed record TimelineRecordExport(
    int Id,
    string Kind,
    string Label,
    string Visibility,
    int? ParentId,
    int Depth,
    string Start,
    long StartTicks,
    double StartSeconds,
    string End,
    long EndTicks,
    double EndSeconds,
    string Duration,
    long DurationTicks,
    double DurationSeconds,
    long? Timestamp,
    string? DeltaTime,
    long? DeltaTimeTicks,
    double? DeltaTimeSeconds
);

public sealed record TimelineSampleExport(
    string Time,
    long TimeTicks,
    double TimeSeconds,
    IReadOnlyList<int> ActiveRecordIds
);

public static class TimelineExportExtensions
{
    private const string Schema = "async-playback.timeline.v1";

    public static PlaybackTimelineExport ExportTimeline(
        this Playback playback,
        TimelineExportOptions? options = null
    )
    {
        if (playback == null)
            throw new ArgumentNullException(nameof(playback));

        options ??= new TimelineExportOptions();

        var records = playback.Records;
        var visibleRecords = options.IncludeInfrastructureRecords
            ? records
            : records
                .Where(static record => record.Visibility == TimelineRecordVisibility.Workflow)
                .ToArray();
        var exportedRecords = visibleRecords.Select(ExportRecord).ToArray();

        var samples = options.SampleInterval is { } interval
            ? ExportSamples(visibleRecords, interval, options.IncludePointsInSamples)
            : [];

        return new PlaybackTimelineExport(
            Schema,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            new TimelinePlaybackExport(
                FormatTime(playback.Time),
                playback.Time.Ticks,
                ToSeconds(playback.Time),
                FormatTime(playback.TargetTime),
                playback.TargetTime.Ticks,
                ToSeconds(playback.TargetTime),
                playback.CurrentDirection.ToString(),
                playback.Mode.ToString(),
                playback.IsCompleted,
                playback.Timestamp,
                FormatTime(playback.DeltaTime),
                playback.DeltaTime.Ticks,
                ToSeconds(playback.DeltaTime)
            ),
            exportedRecords,
            samples
        );
    }

    public static string ExportTimelineJson(
        this Playback playback,
        TimelineExportOptions? options = null,
        JsonSerializerOptions? jsonOptions = null
    )
    {
        jsonOptions ??= new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

        return JsonSerializer.Serialize(playback.ExportTimeline(options), jsonOptions);
    }

    private static TimelineRecordExport ExportRecord(TimelineRecordInfo record)
    {
        return new TimelineRecordExport(
            record.Id,
            record.Kind.ToString(),
            record.DebugLabel,
            record.Visibility.ToString(),
            record.ParentId,
            record.Depth,
            FormatTime(record.StartTime),
            record.StartTime.Ticks,
            ToSeconds(record.StartTime),
            FormatTime(record.EndTime),
            record.EndTime.Ticks,
            ToSeconds(record.EndTime),
            FormatTime(record.Duration),
            record.Duration.Ticks,
            ToSeconds(record.Duration),
            record.Timestamp,
            record.DeltaTime is { } delta ? FormatTime(delta) : null,
            record.DeltaTime?.Ticks,
            record.DeltaTime is { } deltaSeconds ? ToSeconds(deltaSeconds) : null
        );
    }

    private static IReadOnlyList<TimelineSampleExport> ExportSamples(
        IReadOnlyList<TimelineRecordInfo> records,
        TimeSpan interval,
        bool includePoints
    )
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "Sample interval must be positive."
            );

        if (records.Count == 0)
            return [];

        var maxTime = records.Max(static record => record.EndTime);
        var samples = new List<TimelineSampleExport>();

        for (var ticks = 0L; ticks <= maxTime.Ticks; ticks += interval.Ticks)
            samples.Add(ExportSample(TimeSpan.FromTicks(ticks), records, includePoints));

        if (samples.Count == 0 || samples[^1].TimeTicks != maxTime.Ticks)
            samples.Add(ExportSample(maxTime, records, includePoints));

        return samples;
    }

    private static TimelineSampleExport ExportSample(
        TimeSpan time,
        IReadOnlyList<TimelineRecordInfo> records,
        bool includePoints
    )
    {
        var active = records
            .Where(record => IsActiveAt(record, time, includePoints))
            .Select(static record => record.Id.Value)
            .ToArray();

        return new TimelineSampleExport(FormatTime(time), time.Ticks, ToSeconds(time), active);
    }

    private static bool IsActiveAt(TimelineRecordInfo record, TimeSpan time, bool includePoints)
    {
        if (record.Duration == TimeSpan.Zero)
            return includePoints && record.StartTime == time;

        return record.StartTime <= time && time <= record.EndTime;
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.ToString("c", CultureInfo.InvariantCulture);
    }

    private static double ToSeconds(TimeSpan time)
    {
        return time.Ticks / (double)TimeSpan.TicksPerSecond;
    }
}
