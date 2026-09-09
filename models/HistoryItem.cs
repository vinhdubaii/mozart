using System;

namespace MozartBrowser.Models
{
    public class HistoryItem
    {
        public int Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? FaviconUrl { get; set; }
        public DateTime VisitedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Aggregated per-domain visit count, used to build the New Tab "Top Sites" grid.</summary>
    public class TopSiteItem
    {
        public string Domain { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? FaviconUrl { get; set; }
        public int VisitCount { get; set; }
        public bool IsPinned { get; set; }
    }
}
