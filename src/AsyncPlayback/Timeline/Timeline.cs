using System.Runtime.InteropServices;

namespace AsyncPlayback;

internal sealed class Timeline
{
    private readonly Playback playback;
    private readonly List<TimelineRecord> records = [];
    private readonly Dictionary<RecordId, int> indexesById = [];
    private readonly List<TimelineBoundary> traversalBoundaries = [];
    private readonly List<TimelineBoundary> stepForwardBoundaries = [];
    private readonly List<TimelineBoundary> stepBackwardBoundaries = [];
    private readonly Dictionary<TimeSpan, List<EvaluationPoint>> pointEvaluations = [];
    private readonly List<EvaluationRange> rangeEvaluations = [];
    private readonly HashSet<TimeSpan> timedBoundaryTimes = [];
    private readonly Dictionary<TimeSpan, List<int>> seekLoopEnds = [];
    private bool indexesDirty = true;

    public Timeline(Playback playback) => this.playback = playback;

    public int Count => records.Count;
    public TimelineRecord? Last => records.Count == 0 ? null : records[^1];
    public int NextIdSeed => records.Count == 0 ? 0 : records.Max(static r => r.Id.Value);
    public TimelineRecord this[int index]
    {
        get => records[index];
        set
        {
            records[index] = value;
            Invalidate();
        }
    }
    public TimeSpan RecordedEndTime
    {
        get
        {
            var end = TimeSpan.Zero;
            foreach (var record in records)
                if (record.EndTime > end)
                    end = record.EndTime;
            return end;
        }
    }

    public void Add(TimelineRecord record)
    {
        records.Add(record);
        Invalidate();
    }

    public void Invalidate() => indexesDirty = true;

    public void TruncateFrom(int index)
    {
        index = Math.Clamp(index, 0, records.Count);
        if (index < records.Count)
            records.RemoveRange(index, records.Count - index);
        RebuildRecordIndexes();
    }

    public ref TimelineRecord GetRef(int index)
    {
        Invalidate();
        if ((uint)index >= (uint)records.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ref CollectionsMarshal.AsSpan(records)[index];
    }

    public TimelineRecord Get(int index)
    {
        if ((uint)index >= (uint)records.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return records[index];
    }

    public TimelineRecord Get(RecordId recordId) => records[GetIndex(recordId)];

    public int GetIndex(RecordId recordId)
    {
        EnsureIndexes();
        return indexesById.TryGetValue(recordId, out var index)
            ? index
            : throw new InvalidOperationException($"Timeline record #{recordId} does not exist.");
    }

    public IReadOnlyList<TimelineRecordInfo> ToInfos()
    {
        var result = new TimelineRecordInfo[records.Count];
        for (var i = 0; i < records.Count; i++)
            result[i] = records[i].ToInfo();
        return result;
    }

    public IReadOnlyList<TimelineRecordInfo> GetNearestRecords(
        TimeSpan time,
        int count,
        int currentIndex
    )
    {
        return records
            .OrderBy(record => AbsTicks(record.StartTime - time))
            .ThenBy(record =>
                currentIndex < 0 ? record.FlatIndex : Math.Abs(record.FlatIndex - currentIndex)
            )
            .ThenBy(record => record.FlatIndex)
            .Take(count)
            .Select(static record => record.ToInfo())
            .ToArray();
    }

    public void RebuildRecordIndexes()
    {
        indexesById.Clear();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            record.FlatIndex = i;
            indexesById[record.Id] = i;
            records[i] = record;
        }
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.ParentId is { } id && indexesById.TryGetValue(id, out var parentIndex))
            {
                record.ParentIndex = parentIndex;
                record.Depth = records[parentIndex].Depth + 1;
            }
            else
            {
                record.ParentIndex = null;
                record.ParentId = null;
                record.Depth = 0;
            }
            records[i] = record;
        }
        Invalidate();
    }

    public IReadOnlyList<TimelineBoundary> GetBoundaries(TimelineBoundaryScope scope)
    {
        EnsureIndexes();
        return scope switch
        {
            TimelineBoundaryScope.Traversal => traversalBoundaries,
            TimelineBoundaryScope.StepForward => stepForwardBoundaries,
            TimelineBoundaryScope.StepBackward => stepBackwardBoundaries,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
    }

    public bool HasTimedBoundaryAt(TimeSpan time)
    {
        EnsureIndexes();
        return timedBoundaryTimes.Contains(time);
    }

    public bool HasLaterRecordForRunner(TimelineRecord record)
    {
        for (var i = record.FlatIndex + 1; i < records.Count; i++)
            if (ReferenceEquals(records[i].OwnerRunner, record.OwnerRunner))
                return true;
        return false;
    }

    public bool HasSeekLoopEndingAt(TimeSpan time, int beforeRecordIndex)
    {
        EnsureIndexes();
        if (!seekLoopEnds.TryGetValue(time, out var indexes))
            return false;
        foreach (var index in indexes)
            if (index < beforeRecordIndex)
                return true;
        return false;
    }

    public TimelineRecord? FindNearestRecordAtOrBefore(TimeSpan targetTime)
    {
        TimelineRecord? best = null;
        foreach (var record in records)
            if (
                record.StartTime <= targetTime
                && (best == null || record.FlatIndex > best.Value.FlatIndex)
            )
                best = record;
        return best;
    }

    public List<TimelineRecord> GetEvaluationCandidates(
        in RecordEvaluationQuery query,
        HashSet<RecordId> evaluatedIds
    )
    {
        EnsureIndexes();
        var indexes = new HashSet<int>();
        if (pointEvaluations.TryGetValue(query.Time, out var points))
            foreach (var point in points)
                if (point.Matches(query.Direction))
                    indexes.Add(point.RecordIndex);
        foreach (var range in rangeEvaluations)
            if (range.Contains(query.Time, query.Direction))
                indexes.Add(range.RecordIndex);
        var result = new List<TimelineRecord>(indexes.Count);
        foreach (var index in indexes)
        {
            var record = records[index];
            if (!evaluatedIds.Contains(record.Id) && record.Behavior.IsEvaluatable(record, query))
                result.Add(record);
        }
        result.Sort(
            query.Direction == PlaybackDirection.Forward
                ? static (a, b) => a.FlatIndex.CompareTo(b.FlatIndex)
                : static (a, b) => b.FlatIndex.CompareTo(a.FlatIndex)
        );
        return result;
    }

    internal void AddPointEvaluation(TimeSpan time, int index, PlaybackDirection? direction)
    {
        if (!pointEvaluations.TryGetValue(time, out var points))
            pointEvaluations[time] = points = [];
        points.Add(new(index, direction));
    }

    internal void AddRangeEvaluation(
        TimeSpan start,
        TimeSpan end,
        int index,
        PlaybackDirection? direction,
        bool includeEnd
    ) => rangeEvaluations.Add(new(start, end, index, direction, includeEnd));

    private void EnsureIndexes()
    {
        if (!indexesDirty)
            return;
        indexesById.Clear();
        traversalBoundaries.Clear();
        stepForwardBoundaries.Clear();
        stepBackwardBoundaries.Clear();
        pointEvaluations.Clear();
        rangeEvaluations.Clear();
        timedBoundaryTimes.Clear();
        seekLoopEnds.Clear();
        for (var i = 0; i < records.Count; i++)
            IndexRecord(i);
        BuildBoundaries(TimelineBoundaryScope.Traversal, null, traversalBoundaries);
        BuildBoundaries(TimelineBoundaryScope.StepForward, null, stepForwardBoundaries);
        BuildBoundaries(
            TimelineBoundaryScope.StepBackward,
            timedBoundaryTimes,
            stepBackwardBoundaries
        );
        for (var i = 0; i < records.Count; i++)
            IndexEvaluationEntries(i);
        traversalBoundaries.Sort();
        stepForwardBoundaries.Sort();
        stepBackwardBoundaries.Sort();
        indexesDirty = false;
    }

    private void IndexRecord(int index)
    {
        var record = records[index];
        indexesById[record.Id] = index;
        if (record.Behavior is not TimelineRecordBehavior builtIn)
            return;
        builtIn.AddTimedBoundaryTimes(record, timedBoundaryTimes);
        if (!builtIn.HasSeekLoopEndingAt(record, record.EndTime))
            return;
        if (!seekLoopEnds.TryGetValue(record.EndTime, out var indexes))
            seekLoopEnds[record.EndTime] = indexes = [];
        indexes.Add(index);
    }

    private void IndexEvaluationEntries(int index)
    {
        var record = records[index];
        record.Behavior.AddEvaluationEntries(record, new(this, index));
        var boundaries = new List<TimelineBoundary>(2);
        record.Behavior.AddBoundaries(
            record,
            new TimelineBoundaryBuilder(playback, TimelineBoundaryScope.Traversal, null, boundaries)
        );
        foreach (var boundary in boundaries)
            AddPointEvaluation(boundary.Time, index, null);
    }

    private void BuildBoundaries(
        TimelineBoundaryScope scope,
        HashSet<TimeSpan>? timedTimes,
        List<TimelineBoundary> target
    )
    {
        var builder = new TimelineBoundaryBuilder(playback, scope, timedTimes, target);
        for (var i = 0; i < records.Count; i++)
            records[i].Behavior.AddBoundaries(records[i], builder);
    }

    private static long AbsTicks(TimeSpan value) =>
        value.Ticks == long.MinValue ? long.MaxValue : Math.Abs(value.Ticks);

    private readonly record struct EvaluationPoint(int RecordIndex, PlaybackDirection? Direction)
    {
        public bool Matches(PlaybackDirection direction) =>
            Direction == null || Direction == direction;
    }

    private readonly record struct EvaluationRange(
        TimeSpan Start,
        TimeSpan End,
        int RecordIndex,
        PlaybackDirection? Direction,
        bool IncludeEnd
    )
    {
        public bool Contains(TimeSpan time, PlaybackDirection direction) =>
            (Direction == null || Direction == direction)
            && Start <= time
            && (IncludeEnd ? time <= End : time < End);
    }
}
