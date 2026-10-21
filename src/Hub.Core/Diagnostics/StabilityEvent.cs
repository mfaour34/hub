namespace Hub.Core.Diagnostics;

/// <summary>
/// One entry of the crash log: a GPU reset, a hardware error, a blue screen or an unexpected restart.
/// </summary>
/// <param name="Key">Identity for de-duplication (Windows Error Reporting re-posts the same report many times).</param>
/// <param name="Kind">"gpu-reset", "hardware-error", "bluescreen" or "unexpected-restart".</param>
/// <param name="Log">Windows event log the record came from ("System" or "Application"), for Event Viewer.</param>
public sealed record StabilityEvent(string Key, DateTimeOffset Time, string Kind, string Title, string Detail, string Log, long RecordId);
