using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace FluentDesigner.ECS.System;

public sealed class CsvMarkerService : Service, IDisposable
{
    private List<CsvMarker> _markers = [];
    private string _currentFilePath;
    private bool _isLoaded;

    public IReadOnlyList<CsvMarker> Markers => _markers;
    public bool IsLoaded => _isLoaded;
    public int MarkerCount => _markers.Count;
    public string CurrentFilePath => _currentFilePath;
    public float MaxTimeSeconds => _markers.Count > 0
        ? _markers.Max(m => m.EndTimeSeconds)
        : 0f;
    public event EventHandler<IReadOnlyList<CsvMarker>> MarkersLoaded;
    public event EventHandler MarkersCleared;
    public event EventHandler<CsvImportResult> LoadFailed;

    public async UniTask<CsvImportResult> LoadFromFileAsync(string filePath, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            var result = new CsvImportResult
            {
                Success = false,
                ErrorMessage = "File not found."
            };
            LoadFailed?.Invoke(this, result);
            return result;
        }

        return await UniTask.Run(() => LoadFromFileInternal(filePath, token));
    }

    public CsvImportResult LoadFromFileSync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            var result = new CsvImportResult
            {
                Success = false,
                ErrorMessage = "File not found."
            };
            LoadFailed?.Invoke(this, result);
            return result;
        }

        return LoadFromFileInternal(filePath, CancellationToken.None);
    }

    private CsvImportResult LoadFromFileInternal(string filePath, CancellationToken token)
    {
        var newMarkers = new List<CsvMarker>();
        int lineNumber = 1; // 从1开始，表头

        try
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = "\t",
                HasHeaderRecord = true,
                MissingFieldFound = null,
                HeaderValidated = null,
                TrimOptions = TrimOptions.Trim
            };

            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, config);

            csv.Context.RegisterClassMap<CsvMarkerRawMap>();

            // 读取表头
            csv.Read();
            csv.ReadHeader();
            lineNumber++;

            while (csv.Read())
            {
                token.ThrowIfCancellationRequested();
                lineNumber++;

                var raw = csv.GetRecord<CsvMarkerRaw>();
                if (raw == null)
                {
                    continue;
                }
                if (!CsvTimeFormatParser.TryParse(raw.TimeFormat, out var timeFormat))
                {
                    return CreateErrorResult(
                        $"Invalid time format '{raw.TimeFormat}'. Valid formats: {string.Join(", ", CsvTimeFormatParser.ValidFormatNames)}",
                        lineNumber,
                        "Time Format");
                }
                if (!CsvMarkerTypeParser.TryParse(raw.Type, out var markerType))
                {
                    return CreateErrorResult(
                        $"Invalid marker type '{raw.Type}'. Valid types: {string.Join(", ", CsvMarkerTypeParser.ValidTypeNames)}",
                        lineNumber,
                        "Type");
                }
                if (!CsvMarkerTimeParser.TryParse(raw.Start, out var startTime))
                {
                    return CreateErrorResult(
                        $"Invalid start time format '{raw.Start}'. Expected format: M:SS.mmm (e.g., 0:08.891)",
                        lineNumber,
                        "Start");
                }
                if (!CsvMarkerTimeParser.TryParse(raw.Duration, out var duration))
                {
                    return CreateErrorResult(
                        $"Invalid duration format '{raw.Duration}'. Expected format: M:SS.mmm (e.g., 0:00.000)",
                        lineNumber,
                        "Duration");
                }

                var marker = new CsvMarker
                {
                    Name = raw.Name ?? $"Marker_{lineNumber}",
                    StartTimeSeconds = startTime,
                    DurationSeconds = duration,
                    TimeFormat = timeFormat,
                    Type = markerType,
                    Description = raw.Description ?? string.Empty
                };

                newMarkers.Add(marker);
            }

            newMarkers.Sort((a, b) => a.StartTimeSeconds.CompareTo(b.StartTimeSeconds));
            _markers.Clear();
            _markers = newMarkers;
            _currentFilePath = filePath;
            _isLoaded = true;

            var successResult = new CsvImportResult
            {
                Success = true,
                MarkerCount = _markers.Count,
                MaxTimeSeconds = MaxTimeSeconds
            };

            MarkersLoaded?.Invoke(this, _markers);
            return successResult;
        }
        catch (OperationCanceledException)
        {
            return new CsvImportResult
            {
                Success = false,
                ErrorMessage = "Import was cancelled."
            };
        }
        catch (Exception ex)
        {
            var result = new CsvImportResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse CSV file: {ex.Message}",
                ErrorLineNumber = lineNumber
            };
            LoadFailed?.Invoke(this, result);
            return result;
        }
    }

    private CsvImportResult CreateErrorResult(string message, int lineNumber, string fieldName)
    {
        var result = new CsvImportResult
        {
            Success = false,
            ErrorMessage = message,
            ErrorLineNumber = lineNumber,
            ErrorFieldName = fieldName
        };
        LoadFailed?.Invoke(this, result);
        return result;
    }

    public IEnumerable<CsvMarker> GetMarkersInRange(float startTime, float endTime)
    {
        if (!_isLoaded || _markers.Count == 0)
        {
            yield break;
        }

        foreach (var marker in _markers)
        {
            bool startInRange = marker.StartTimeSeconds >= startTime && marker.StartTimeSeconds <= endTime;
            bool endInRange = marker.EndTimeSeconds >= startTime && marker.EndTimeSeconds <= endTime;
            bool spansRange = marker.StartTimeSeconds < startTime && marker.EndTimeSeconds > endTime;

            if (startInRange || endInRange || spansRange)
            {
                yield return marker;
            }
        }
    }

    public CsvMarker GetNearestMarker(float timeSeconds, float tolerance = 0.1f)
    {
        if (!_isLoaded || _markers.Count == 0)
        {
            return null;
        }

        CsvMarker nearest = null;
        float minDistance = float.MaxValue;

        foreach (var marker in _markers)
        {
            float distance = Math.Abs(marker.StartTimeSeconds - timeSeconds);
            if (distance < minDistance && distance <= tolerance)
            {
                minDistance = distance;
                nearest = marker;
            }
            if (marker.HasDuration)
            {
                float endDistance = Math.Abs(marker.EndTimeSeconds - timeSeconds);
                if (endDistance < minDistance && endDistance <= tolerance)
                {
                    minDistance = endDistance;
                    nearest = marker;
                }
            }
        }

        return nearest;
    }

    public IEnumerable<CsvMarker> GetMarkersByType(CsvMarkerType type)
    {
        return _markers.Where(m => m.Type == type);
    }

    public void ClearMarkers()
    {
        _markers.Clear();
        _currentFilePath = null;
        _isLoaded = false;
        MarkersCleared?.Invoke(this, EventArgs.Empty);
    }

    public override void Shutdown()
    {
        ClearMarkers();
        base.Shutdown();
    }

    public void Dispose()
    {
        Shutdown();
    }
}
