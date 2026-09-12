using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Data
{
    /// <summary>
    /// Owns the "passwords" table in browser.db — Mozart's own password vault,
    /// completely separate from WebView2/Chromium's built-in autosave (which
    /// the app cannot read back). Follows the exact same SqliteConnection
    /// pattern as HistoryService/BookmarkService (one short-lived connection
    /// per call — this is a low-traffic table, no pooling needed).
    ///
    /// Encryption: System.Security.Cryptography.ProtectedData (Windows DPAPI),
    /// scoped with DataProtectionScope.CurrentUser — the ciphertext can only
    /// be decrypted by the same Windows user account on the same machine,
    /// which is exactly the technology Chromium itself uses to protect its
    /// own password store. There is no separate master password; anyone
    /// logged into this Windows account can decrypt, matching how the OS
    /// itself already gates access (same trust boundary as the Windows
    /// login). Private tabs must never call into this service — that check
    /// happens at the call site (MainWindow), mirroring HistoryService.
    /// </summary>
    public class PasswordVaultService
    {
        private readonly string _connectionString;

        /// <summary>
        /// Optional "entropy" mixed into every DPAPI call. Not a secret by
        /// itself (it ships in the binary), but it stops a different app
        /// running under the same Windows account from decrypting this
        /// specific vault's blobs by chance — DPAPI alone only scopes to the
        /// user account, not to which app wrote the data.
        /// </summary>
        private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("MozartBrowser.PasswordVault.v1");

        public PasswordVaultService(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public async Task InitializeAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS passwords (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    domain TEXT NOT NULL,
                    username TEXT NOT NULL,
                    encrypted_password BLOB NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(domain, username)
                );
                CREATE INDEX IF NOT EXISTS idx_passwords_domain ON passwords(domain);
            """;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>DPAPI-encrypts <paramref name="plaintextPassword"/> in memory; the plaintext itself is never written to disk.</summary>
        public static byte[] Encrypt(string plaintextPassword)
        {
            var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintextPassword);
            return ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        }

        /// <summary>
        /// Decrypts a stored blob back to plaintext. Throws CryptographicException
        /// if the blob was written by a different Windows user account or a
        /// different machine (expected — vault entries don't roam).
        /// </summary>
        public static string Decrypt(byte[] encryptedPassword)
        {
            var plainBytes = ProtectedData.Unprotect(encryptedPassword, Entropy, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }

        /// <summary>
        /// Saves a new login, or overwrites the password/updated_at of an
        /// existing (domain, username) pair — this is how a changed password
        /// for an already-saved account gets updated instead of duplicated.
        /// </summary>
        public async Task SaveAsync(string domain, string username, string plaintextPassword)
        {
            var encrypted = Encrypt(plaintextPassword);
            var now = DateTime.UtcNow.ToString("O");

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO passwords (domain, username, encrypted_password, created_at, updated_at)
                VALUES ($domain, $username, $encrypted, $createdAt, $updatedAt)
                ON CONFLICT(domain, username) DO UPDATE SET
                    encrypted_password = excluded.encrypted_password,
                    updated_at = excluded.updated_at;
            """;
            cmd.Parameters.AddWithValue("$domain", domain);
            cmd.Parameters.AddWithValue("$username", username);
            cmd.Parameters.AddWithValue("$encrypted", encrypted);
            cmd.Parameters.AddWithValue("$createdAt", now);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>All saved logins for exactly this domain (host match, no wildcard/subdomain fuzziness) — used for click-to-fill.</summary>
        public async Task<List<SavedPassword>> GetForDomainAsync(string domain)
        {
            var results = new List<SavedPassword>();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, domain, username, encrypted_password, created_at, updated_at
                FROM passwords
                WHERE domain = $domain
                ORDER BY updated_at DESC;
            """;
            cmd.Parameters.AddWithValue("$domain", domain);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadRow(reader));

            return results;
        }

        /// <summary>Whether (domain, username) is already saved — lets the capture flow offer "Update" instead of a duplicate "Save".</summary>
        public async Task<bool> ExistsAsync(string domain, string username)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM passwords WHERE domain = $domain AND username = $username LIMIT 1;";
            cmd.Parameters.AddWithValue("$domain", domain);
            cmd.Parameters.AddWithValue("$username", username);

            var result = await cmd.ExecuteScalarAsync();
            return result != null;
        }

        /// <summary>Every saved login, grouped by nothing in particular — PasswordManagerWindow does its own domain grouping/search over this.</summary>
        public async Task<List<SavedPassword>> GetAllAsync()
        {
            var results = new List<SavedPassword>();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, domain, username, encrypted_password, created_at, updated_at
                FROM passwords
                ORDER BY domain COLLATE NOCASE, username COLLATE NOCASE;
            """;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadRow(reader));

            return results;
        }

        /// <summary>One saved login by id — used by the passwords.reveal bridge handler right before decrypting for display.</summary>
        public async Task<SavedPassword?> GetByIdAsync(int id)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, domain, username, encrypted_password, created_at, updated_at
                FROM passwords
                WHERE id = $id;
            """;
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadRow(reader) : null;
        }

        public async Task DeleteAsync(int id)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM passwords WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Wipes every saved login — wired into "Delete browsing data" / "Clear on close" when the Passwords category is selected.</summary>
        public async Task ClearAllAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM passwords;";
            await cmd.ExecuteNonQueryAsync();
        }

        private static SavedPassword ReadRow(SqliteDataReader reader) => new()
        {
            Id = reader.GetInt32(0),
            Domain = reader.GetString(1),
            Username = reader.GetString(2),
            EncryptedPassword = (byte[])reader["encrypted_password"],
            CreatedAt = DateTime.Parse(reader.GetString(4)),
            UpdatedAt = DateTime.Parse(reader.GetString(5))
        };
    }
}
