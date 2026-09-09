using System;

namespace MozartBrowser.Models
{
    /// <summary>
    /// One saved login in Mozart's own password vault (table "passwords" in
    /// browser.db) — entirely separate from WebView2/Chromium's built-in
    /// autosave store, which the app cannot read back (no public API).
    ///
    /// EncryptedPassword is the Windows-DPAPI-protected blob (via
    /// System.Security.Cryptography.ProtectedData, scoped to the current
    /// Windows user account — the same technology Chromium itself uses to
    /// encrypt its own password store). It is never decrypted except inside
    /// PasswordVaultService, right before autofilling a page or showing
    /// plaintext in PasswordManagerWindow after a successful Windows Hello /
    /// credential re-check.
    /// </summary>
    public class SavedPassword
    {
        public int Id { get; set; }

        /// <summary>Host only (e.g. "accounts.google.com"), not a full URL — matched exactly against the page's own host.</summary>
        public string Domain { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        /// <summary>Base64 of the DPAPI-protected ciphertext. Never serialized to JSON or logged.</summary>
        public byte[] EncryptedPassword { get; set; } = Array.Empty<byte>();

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
