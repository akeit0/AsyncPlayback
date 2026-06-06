namespace AsyncPlayback;

public sealed partial class Playback
{
    private static class RecordEvaluator
    {
        public static async ValueTask<bool> EvaluateAtAsync(
            Playback playback,
            TimeSpan time,
            PlaybackDirection direction,
            bool includeLoopEnd
        )
        {
            var evaluatedIds = new HashSet<RecordId>();
            var evaluatedAny = false;
            playback.MoveTimeTo(time);
            while (true)
            {
                var query = new RecordEvaluationQuery(time, direction, includeLoopEnd)
                {
                    Playback = playback,
                };
                if (
                    !playback.timeline.TryGetNextEvaluationCandidate(
                        query,
                        evaluatedIds,
                        out var record
                    )
                )
                    break;
                evaluatedIds.Add(record.Id);
                await EvaluateRecordAsync(playback, record, time, direction).ConfigureAwait(false);
                evaluatedAny = true;
                await playback
                    .RunReadyAsync(playback.currentCancellationToken)
                    .ConfigureAwait(false);
                playback.MoveTimeTo(time);
            }
            return evaluatedAny;
        }

        public static async ValueTask EvaluateTraversalAsync(
            Playback playback,
            TimeSpan targetTime,
            PlaybackDirection direction,
            bool evaluateTarget,
            CancellationToken cancellationToken
        )
        {
            while (
                playback.timeline.TryFindNextEvaluationTime(
                    playback.Time,
                    targetTime,
                    direction,
                    out var time
                )
            )
            {
                await EvaluateAtAsync(playback, time, direction, true).ConfigureAwait(false);
                await playback.RunReadyAsync(cancellationToken).ConfigureAwait(false);
                if (
                    direction == PlaybackDirection.Forward
                    && playback.edgeCursor == PlaybackEdgeCursor.AfterLast
                )
                    return;
                if (playback.Time == targetTime)
                    return;
            }
            if (
                evaluateTarget
                && await EvaluateAtAsync(playback, targetTime, direction, true)
                    .ConfigureAwait(false)
            )
                return;
            playback.MoveToTimelineGap(targetTime);
        }

        public static ValueTask EvaluateRecordAsync(
            Playback playback,
            TimelineRecord record,
            TimeSpan time,
            PlaybackDirection direction
        ) => record.Behavior.EvaluateAsync(playback, record, time, direction);
    }
}
