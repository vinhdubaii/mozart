using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Data
{
    /// <summary>
    /// Owns the "history" table in browser.db. Private tabs must never call
    /// into this service — that check happens at the call site (MainWindow),
    /// keeping this service unaware of private mode entirely.
    /// </summary>
    public class HistoryService
    {
        private readonly string _connectionString;

        public HistoryService(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public async Task InitializeAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    favicon_url TEXT,
                    visited_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_history_visited_at ON history(visited_at);
                CREATE INDEX IF NOT EXISTS idx_history_url ON history(url);

                CREATE TABLE IF NOT EXISTS pinned_sites (
                    url TEXT PRIMARY KEY,
                    title TEXT NOT NULL DEFAULT '',
                    favicon_url TEXT,
                    pinned_at TEXT NOT NULL
                );
            """;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task AddVisitAsync(string url, string title, string? faviconUrl)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO history (url, title, favicon_url, visited_at)
                VALUES ($url, $title, $favicon, $visitedAt);
            """;
            cmd.Parameters.AddWithValue("$url", url);
            cmd.Parameters.AddWithValue("$title", title ?? string.Empty);
            cmd.Parameters.AddWithValue("$favicon", (object?)faviconUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$visitedAt", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<HistoryItem>> GetRecentAsync(int limit = 200)
        {
            var results = new List<HistoryItem>();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, url, title, favicon_url, visited_at
                FROM history
                ORDER BY visited_at DESC
                LIMIT $limit;
            """;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new HistoryItem
                {
                    Id = reader.GetInt32(0),
                    Url = reader.GetString(1),
                    Title = reader.GetString(2),
                    FaviconUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    VisitedAt = DateTime.Parse(reader.GetString(4))
                });
            }

            return results;
        }

        /// <summary>Powers the New Tab Page "Top Sites" grid: most-visited domains, most recent first on ties.</summary>
        public async Task<List<TopSiteItem>> GetTopSitesAsync(int limit = 8)
        {
            var results = new List<TopSiteItem>();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT url, title, favicon_url, COUNT(*) as visits, MAX(visited_at) as last_visit
                FROM history
                GROUP BY url
                ORDER BY visits DESC, last_visit DESC
                LIMIT $limit;
            """;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var url = reader.GetString(0);
                results.Add(new TopSiteItem
                {
                    Url = url,
                    Domain = TryGetDomain(url),
                    Title = reader.GetString(1),
                    FaviconUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                    VisitCount = reader.GetInt32(3)
                });
            }

            return results;
        }

        /// <summary>Case-insensitive substring match against url/title, most recent first. Powers history.html's search box.</summary>
        public async Task<List<HistoryItem>> SearchAsync(string query, int limit = 200)
        {
            var results = new List<HistoryItem>();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, url, title, favicon_url, visited_at
                FROM history
                WHERE url LIKE $pattern OR title LIKE $pattern
                ORDER BY visited_at DESC
                LIMIT $limit;
            """;
            cmd.Parameters.AddWithValue("$pattern", $"%{query}%");
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new HistoryItem
                {
                    Id = reader.GetInt32(0),
                    Url = reader.GetString(1),
                    Title = reader.GetString(2),
                    FaviconUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    VisitedAt = DateTime.Parse(reader.GetString(4))
                });
            }

            return results;
        }

        /// <summary>Deletes a single visit row. Powers the per-row delete button in history.html.</summary>
        public async Task DeleteAsync(int id)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM history WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task ClearAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM history;";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Sites the user explicitly pinned to the New Tab Page. Kept in a
        /// separate table from history (not just a flag on a history row)
        /// because a site can be pinned via AddPinnedSiteDialog with no visit
        /// history at all.
        /// </summary>
        public async Task<List<TopSiteItem>> GetPinnedSitesAsync()
        {
            var results = new List<TopSiteItem>();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT url, title, favicon_url
                FROM pinned_sites
                ORDER BY pinned_at DESC;
            """;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var url = reader.GetString(0);
                results.Add(new TopSiteItem
                {
                    Url = url,
                    Domain = TryGetDomain(url),
                    Title = reader.GetString(1),
                    FaviconUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsPinned = true
                });
            }

            return results;
        }

        /// <summary>Pins a site, or updates its title/favicon if it was already pinned.</summary>
        public async Task PinSiteAsync(string url, string title, string? faviconUrl)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pinned_sites (url, title, favicon_url, pinned_at)
                VALUES ($url, $title, $favicon, $pinnedAt)
                ON CONFLICT(url) DO UPDATE SET
                    title = excluded.title,
                    favicon_url = excluded.favicon_url;
            """;
            cmd.Parameters.AddWithValue("$url", url);
            cmd.Parameters.AddWithValue("$title", title ?? string.Empty);
            cmd.Parameters.AddWithValue("$favicon", (object?)faviconUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pinnedAt", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task UnpinSiteAsync(string url)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM pinned_sites WHERE url = $url;";
            cmd.Parameters.AddWithValue("$url", url);
            await cmd.ExecuteNonQueryAsync();
        }

        private static string TryGetDomain(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        }
    }
}
