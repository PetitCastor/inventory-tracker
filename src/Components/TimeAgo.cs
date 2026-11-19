namespace InventoryTracker.Components;

/// <summary>How long ago something happened, in the short form the tables and status bar use.</summary>
public static class TimeAgo
{
    public static string Format(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        return span switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{span.TotalMinutes:F0} min ago",
            { TotalDays: < 1 } => $"{span.TotalHours:F0} h ago",
            { TotalDays: < 30 } => $"{span.TotalDays:F0} d ago",
            _ => when.ToString("yyyy-MM-dd"),
        };
    }
}
