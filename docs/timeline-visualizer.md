# Timeline Visualizer

AsyncPlayback can export its recorded timeline as JSON and load that JSON in a
small browser viewer.

## Export JSON

Use `ExportTimelineJson()` after running or moving playback:

```csharp
await playback.RunToEndAsync();

var json = playback.ExportTimelineJson(
    new TimelineExportOptions
    {
        SampleInterval = TimeSpan.FromMilliseconds(50),
    }
);

await File.WriteAllTextAsync("timeline.json", json);
```

`ExportTimeline()` returns the same data as objects if code wants to inspect it
without serializing.

## JSON Shape

The export contains two views of the same playback:

- `records`: timeline records such as calls, effects, delays, seek loops, and
  checkpoints. Ranged records have `startSeconds`, `endSeconds`, and
  `durationSeconds`. `parentId` and `depth` describe the record tree.
  `visibility` is `Workflow` for user-facing records and `Infrastructure` for
  internal entry/continuation checkpoints.
- `samples`: optional periodic samples. Each sample lists the record ids active
  at that sampled playback time.

Ticks and formatted `TimeSpan` strings are included too. Use the numeric seconds
or ticks for tooling; use the strings for display.

## Viewer

Open `tools/timeline-viewer/index.html` in a browser, then choose the exported
JSON file.

The viewer uses `vis-timeline` because the data is naturally an interactive
timeline with ranged items and point markers. It packs records into hierarchical
lanes: parent spans and non-overlapping sibling spans share the upper lanes,
while nested child records move to deeper lanes. Checkpoint points use dedicated
marker lanes so they do not cover adjacent spans. Internal entry/continuation
checkpoints are hidden by default; enable `Show internals` when debugging the
runtime. The viewer does not add a runtime dependency to the library; only the
standalone HTML file loads the JavaScript visualization library.

`sandbox/ConsoleApp3` writes `sandbox/ConsoleApp3/obj/timeline.json` after its
forward pass, so it can be used as a quick sample.
