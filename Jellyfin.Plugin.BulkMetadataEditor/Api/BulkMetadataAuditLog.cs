using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkMetadataEditor.Api;

/// <summary>
/// Persists a record of every bulk edit as a JSON file.
/// </summary>
public sealed class BulkMetadataAuditLog
{
    /// <summary>
    /// The number of bulk edits kept; older entries are dropped.
    /// </summary>
    public const int MaxEntries = 100;

    /// <summary>
    /// The number of items whose individual results are kept per entry. Counts always cover every item.
    /// </summary>
    public const int MaxItemsPerEntry = 500;

    private const int MaxValueLength = 80;

    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkMetadataAuditLog"/> class.
    /// </summary>
    /// <param name="filePath">The audit log file.</param>
    /// <param name="logger">The logger.</param>
    public BulkMetadataAuditLog(string filePath, ILogger logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>
    /// Reads all entries, newest first.
    /// </summary>
    /// <returns>The audit log entries.</returns>
    public List<AuditEntry> ReadEntries()
    {
        lock (FileLock)
        {
            return ReadEntriesInternal();
        }
    }

    /// <summary>
    /// Records one batch of a bulk edit. Batches sharing an operation ID are merged into one entry.
    /// </summary>
    /// <param name="request">The request that was applied.</param>
    /// <param name="response">The results of applying it.</param>
    /// <param name="userName">The user who made the change.</param>
    public void Record(BulkUpdateRequest request, BulkUpdateResponse response, string? userName)
    {
        if (response.Results.Count == 0)
        {
            return;
        }

        lock (FileLock)
        {
            var entries = ReadEntriesInternal();
            var entry = request.OperationId == Guid.Empty
                ? null
                : entries.FirstOrDefault(e => e.Id == request.OperationId);

            if (entry is null)
            {
                entry = new AuditEntry
                {
                    Id = request.OperationId == Guid.Empty ? Guid.NewGuid() : request.OperationId,
                    TimestampUtc = DateTime.UtcNow,
                    UserName = userName,
                    LockEditedFields = request.LockEditedFields,
                    Changes = DescribeChanges(request.Changes)
                };
                entries.Insert(0, entry);
            }

            foreach (var result in response.Results)
            {
                entry.ItemCount++;
                if (result.Success)
                {
                    entry.UpdatedCount++;
                }
                else
                {
                    entry.FailedCount++;
                }

                if (entry.Items.Count < MaxItemsPerEntry)
                {
                    entry.Items.Add(new AuditItem
                    {
                        Id = result.Id,
                        Name = result.Name,
                        Type = result.Type,
                        Success = result.Success,
                        Error = result.Error,
                        Skipped = result.Skipped
                    });
                }
                else
                {
                    entry.OmittedItemCount++;
                }
            }

            entry.CompletedUtc = DateTime.UtcNow;
            SaveEntries(entries.Take(MaxEntries).ToList());
        }
    }

    /// <summary>
    /// Removes every entry.
    /// </summary>
    public void Clear()
    {
        lock (FileLock)
        {
            SaveEntries([]);
        }
    }

    /// <summary>
    /// Describes each requested change as one human-readable line, e.g. "Genres: add Noir, Drama".
    /// </summary>
    /// <param name="changes">The requested changes.</param>
    /// <returns>One line per changed field.</returns>
    public static List<string> DescribeChanges(MetadataChanges changes)
    {
        var lines = new List<string>();
        foreach (var property in typeof(MetadataChanges).GetProperties())
        {
            var value = property.GetValue(changes);
            if (value is null)
            {
                continue;
            }

            var description = value switch
            {
                TextChange c => SetOrClear(c.Value is null ? null : Quote(c.Value)),
                NumberChange c => SetOrClear(c.Value?.ToString(CultureInfo.InvariantCulture)),
                IntegerChange c => SetOrClear(c.Value?.ToString(CultureInfo.InvariantCulture)),
                DateChange c => SetOrClear(c.Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                BooleanChange c => "set to " + (c.Value ? "yes" : "no"),
                ListChange c => DescribeList(c.Mode, c.Values),
                PeopleChange c => DescribeList(c.Mode, c.People.Select(p => string.IsNullOrEmpty(p.Type) ? p.Name : $"{p.Name} ({p.Type})")),
                ProviderIdsChange c => DescribeProviderIds(c),
                _ => "changed"
            };

            lines.Add($"{property.Name}: {description}");
        }

        return lines;
    }

    private static string SetOrClear(string? value) => string.IsNullOrWhiteSpace(value) ? "cleared" : "set to " + value;

    private static string Quote(string value)
    {
        var trimmed = value.Trim();
        return "\"" + (trimmed.Length > MaxValueLength ? trimmed[..MaxValueLength] + "…" : trimmed) + "\"";
    }

    private static string DescribeList(ListMode mode, IEnumerable<string> values)
    {
        var joined = string.Join(", ", values);
        if (mode == ListMode.Replace && joined.Length == 0)
        {
            return "cleared";
        }

        return mode.ToString().ToLowerInvariant() + " " + joined;
    }

    private static string DescribeProviderIds(ProviderIdsChange change)
    {
        var parts = change.Set.Select(kv => string.IsNullOrWhiteSpace(kv.Value) ? "remove " + kv.Key : $"set {kv.Key}={kv.Value}")
            .Concat(change.Remove.Select(key => "remove " + key));
        return string.Join(", ", parts);
    }

    private List<AuditEntry> ReadEntriesInternal()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<AuditEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read bulk metadata audit log from {FilePath}", _filePath);
            return [];
        }
    }

    private void SaveEntries(List<AuditEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            // Write to a temporary file first so a crash mid-write cannot leave a truncated log.
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(tempPath, _filePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write bulk metadata audit log to {FilePath}", _filePath);
        }
    }
}

/// <summary>
/// One bulk edit, which may have been sent as several batches.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; }

    public DateTime TimestampUtc { get; set; }

    public DateTime CompletedUtc { get; set; }

    public string? UserName { get; set; }

    public List<string> Changes { get; set; } = [];

    public bool LockEditedFields { get; set; }

    public int ItemCount { get; set; }

    public int UpdatedCount { get; set; }

    public int FailedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of items beyond <see cref="BulkMetadataAuditLog.MaxItemsPerEntry"/> whose individual results were not kept.
    /// </summary>
    public int OmittedItemCount { get; set; }

    public List<AuditItem> Items { get; set; } = [];
}

/// <summary>
/// The outcome of a bulk edit for one item.
/// </summary>
public sealed class AuditItem
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public List<string> Skipped { get; set; } = [];
}

/// <summary>
/// The audit log as returned by the API.
/// </summary>
public sealed class AuditLogResponse
{
    public List<AuditEntry> Entries { get; set; } = [];
}
