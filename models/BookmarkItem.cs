using System;

namespace MozartBrowser.Models
{
    public class BookmarkItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? FaviconUrl { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Null = root level. Reserved for a future folder feature.</summary>
        public int? FolderId { get; set; }
    }
}
