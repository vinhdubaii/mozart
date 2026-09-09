using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Data
{
    /// <summary>Owns the "bookmarks" table in browser.db (same database file as history, separate table).</summary>
    public class BookmarkService
    {
        private readonly string _connectionString;

        public BookmarkService(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public async Task InitializeAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS bookmarks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    favicon_url TEXT,
                    folder_id INTEGER,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_bookmarks_url ON bookmarks(url);
            """;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<bool> IsBookmarkedAsync(string url)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM bookmarks WHERE url = $url;";
            cmd.Parameters.AddWithValue("$url", url);

            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            return count > 0;
        }

        public async Task AddAsync(string url, string title, string? faviconUrl)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO bookmarks (url, title, favicon_url, created_at)
                VALUES ($url, $title, $favicon, $createdAt);
            """;
            cmd.Parameters.AddWithValue("$url", url);
            cmd.Parameters.AddWithValue("$title", title ?? string.Empty);
            cmd.Parameters.AddWithValue("$favicon", (object?)faviconUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task RemoveAsync(string url)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM bookmarks WHERE url = $url;";
            cmd.Parameters.AddWithValue("$url", url);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<BookmarkItem>> GetAllAsync()
        {
            var results = new List<BookmarkItem>();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, url, title, favicon_url, folder_id, created_at
                FROM bookmarks
                ORDER BY created_at DESC;
            """;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new BookmarkItem
                {
                    Id = reader.GetInt32(0),
                    Url = reader.GetString(1),
                    Title = reader.GetString(2),
                    FaviconUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    FolderId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    CreatedAt = DateTime.Parse(reader.GetString(5))
                });
            }

            return results;
        }
    }
}
