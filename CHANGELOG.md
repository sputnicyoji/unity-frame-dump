# Changelog

## [1.1.2] - 2026-03-18

### Fixed
- **Local export data loss on slow machines**: Replaced fixed 2-retry with timeout-based polling (up to 2s per event). GPU replay is no longer redundantly re-triggered on retry — the tool simply waits for the in-progress replay to complete. Fast machines see no change; slow machines get complete data.

### Changed
- **Export filename**: `fd_{target}_{app}_{timestamp}.json` — includes `editor`/`android`/`ios` target and app name for easy identification
- **JSON `exportMode`**: Renamed from `"async"` to `"full"`

### Added
- **Version label**: Package version displayed in the exporter window (top-right corner)

## [1.1.1] - 2026-03-18

### Fixed
- **Connection mode stuck on Remote**: `GetRemotePlayerGUID` GUID persists after device disconnect, causing permanent Remote mode. Now uses `FrameDebugger.IsRemoteEnabled()` API as primary detection, with GUID as fallback.

### Added
- **Manual connection override**: `Auto / Force Local / Force Remote` dropdown in UI, overrides auto-detection when needed
- **Diagnose detection info**: Shows detection method (`API` vs `GUID-fallback`) and override state

## [1.1.0] - 2026-03-17

### Added
- **Remote device support**: Signal-based wait using `receivingRemoteFrameEventData` for reliable data capture from USB-connected Android devices
- **Auto-detect connection mode**: Uses `GetRemotePlayerGUID` to detect Local/Remote, displayed as colored badge in UI
- **Adaptive retry**: Remote exports retry up to 8 times with re-triggered GPU replay; local exports retry up to 2 times
- **Diagnose connection info**: First diagnose check now shows connection mode and device GUID
- **Dynamic probe indices**: Diagnose adapts to actual event count instead of hardcoded indices
- **Copy Path button**: One-click copy export file path to clipboard

### Changed
- Remote exports use signal-based waiting (poll `receivingRemoteFrameEventData` until transfer completes, 5s timeout per event) instead of fixed frame delays
- Local exports use minimal 1-frame wait for maximum speed
- Removed manual Wait Frames / Max Retries sliders (values are now automatic based on connection mode)
- Progress bar updates more frequently (every 5 events)

### Fixed
- `SetLimit` fallback path no longer does uncached reflection lookup per call (`set_limit` method cached in `DiscoverTypes`)
- Diagnose no longer probes out-of-range event indices when device has fewer than 50 events
- `WriteSummary` sort pattern deduplicated via `SortedDescending` helper
- `TryValidateExportPrerequisites` no longer called twice redundantly in `DrawExportButtons`

## [1.0.0] - 2026-03-17

### Added
- Initial release
- Frame Debugger data export to structured JSON
- Quick Export (names/types only) and Full Export (async, per-event detail)
- Diagnose Limit Setter for API compatibility checks
- Self-check and issue reporting
- Render target timeline tracking
- Batch break cause analysis
