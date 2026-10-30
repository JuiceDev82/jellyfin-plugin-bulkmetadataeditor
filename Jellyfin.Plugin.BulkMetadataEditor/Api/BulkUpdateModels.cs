using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.BulkMetadataEditor.Api;

/// <summary>
/// How a list-valued field is combined with the item's current values.
/// </summary>
public enum ListMode
{
    /// <summary>Add the values that are not already present.</summary>
    Add,

    /// <summary>Remove the given values.</summary>
    Remove,

    /// <summary>Replace the current values entirely.</summary>
    Replace
}

/// <summary>
/// Sets a text field. A null or blank value clears it.
/// </summary>
public class TextChange
{
    public string? Value { get; set; }
}

/// <summary>
/// Sets a decimal field. A null value clears it.
/// </summary>
public class NumberChange
{
    public float? Value { get; set; }
}

/// <summary>
/// Sets an integer field. A null value clears it.
/// </summary>
public class IntegerChange
{
    public int? Value { get; set; }
}

/// <summary>
/// Sets a date field. A null value clears it.
/// </summary>
public class DateChange
{
    public DateTime? Value { get; set; }
}

/// <summary>
/// Sets a boolean field.
/// </summary>
public class BooleanChange
{
    public bool Value { get; set; }
}

/// <summary>
/// Adds, removes or replaces values of a list field.
/// </summary>
public class ListChange
{
    public ListMode Mode { get; set; }

    public string[] Values { get; set; } = [];
}

/// <summary>
/// A person to add or remove.
/// </summary>
public class PersonEntry
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the person kind (Actor, Director, ...). Empty matches any kind when removing.
    /// </summary>
    public string? Type { get; set; }

    public string? Role { get; set; }
}

/// <summary>
/// Adds, removes or replaces the people of an item.
/// </summary>
public class PeopleChange
{
    public ListMode Mode { get; set; }

    public PersonEntry[] People { get; set; } = [];
}

/// <summary>
/// Sets or removes external provider IDs.
/// </summary>
public class ProviderIdsChange
{
    public Dictionary<string, string> Set { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string[] Remove { get; set; } = [];
}

/// <summary>
/// The metadata changes to apply. Only fields that are present are changed.
/// </summary>
public class MetadataChanges
{
    public TextChange? Name { get; set; }

    public TextChange? OriginalTitle { get; set; }

    public TextChange? ForcedSortName { get; set; }

    public TextChange? OriginalLanguage { get; set; }

    public DateChange? DateCreated { get; set; }

    public TextChange? Status { get; set; }

    public DateChange? PremiereDate { get; set; }

    public IntegerChange? ProductionYear { get; set; }

    public ListChange? AirDays { get; set; }

    public TextChange? AirTime { get; set; }

    public DateChange? EndDate { get; set; }

    /// <summary>
    /// Gets or sets the run time in minutes. Only applies to series, as in the stock editor.
    /// </summary>
    public IntegerChange? RunTimeMinutes { get; set; }

    public NumberChange? CommunityRating { get; set; }

    public NumberChange? CriticRating { get; set; }

    public TextChange? Overview { get; set; }

    public TextChange? Tagline { get; set; }

    public TextChange? OfficialRating { get; set; }

    public TextChange? CustomRating { get; set; }

    public ListChange? Genres { get; set; }

    public ListChange? Tags { get; set; }

    public ListChange? Studios { get; set; }

    public ListChange? ProductionLocations { get; set; }

    public PeopleChange? People { get; set; }

    public TextChange? Album { get; set; }

    public ListChange? AlbumArtists { get; set; }

    public ListChange? Artists { get; set; }

    public IntegerChange? IndexNumber { get; set; }

    public IntegerChange? ParentIndexNumber { get; set; }

    public IntegerChange? AirsBeforeSeasonNumber { get; set; }

    public IntegerChange? AirsAfterSeasonNumber { get; set; }

    public IntegerChange? AirsBeforeEpisodeNumber { get; set; }

    public TextChange? AspectRatio { get; set; }

    public TextChange? Video3DFormat { get; set; }

    public ProviderIdsChange? ProviderIds { get; set; }

    public TextChange? PreferredMetadataLanguage { get; set; }

    public TextChange? PreferredMetadataCountryCode { get; set; }

    public TextChange? DisplayOrder { get; set; }

    public BooleanChange? LockData { get; set; }

    public ListChange? LockedFields { get; set; }
}

/// <summary>
/// A bulk update request.
/// </summary>
public class BulkUpdateRequest
{
    public Guid[] ItemIds { get; set; } = [];

    public MetadataChanges Changes { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether lockable fields that were edited are added to each item's locked fields,
    /// so a later metadata refresh does not overwrite them.
    /// </summary>
    public bool LockEditedFields { get; set; }
}

/// <summary>
/// The outcome for one item.
/// </summary>
public class BulkItemResult
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public List<string> Applied { get; set; } = [];

    /// <summary>
    /// Gets or sets the fields that do not apply to this item's type, or had invalid values.
    /// </summary>
    public List<string> Skipped { get; set; } = [];
}

/// <summary>
/// A bulk update response.
/// </summary>
public class BulkUpdateResponse
{
    public List<BulkItemResult> Results { get; set; } = [];
}
