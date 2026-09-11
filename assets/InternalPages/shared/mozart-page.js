/**
 * MozartPage — small shared helpers for internal pages (Settings, History,
 * Downloads, Extensions). Not a framework, just the handful of things every
 * one of these pages would otherwise duplicate.
 */
(function (global) {
    "use strict";

    function escapeHtml(value) {
        return String(value ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    function formatBytes(bytes) {
        if (!bytes || bytes <= 0) return "0 B";
        const units = ["B", "KB", "MB", "GB", "TB"];
        const i = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
        return `${(bytes / Math.pow(1024, i)).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
    }

    /** Groups items by calendar day for display, e.g. history rows. dateGetter maps an item to its ISO date string. */
    function formatDayLabel(isoString) {
        const date = new Date(isoString);
        const today = new Date();
        const yesterday = new Date();
        yesterday.setDate(today.getDate() - 1);

        const sameDay = (a, b) =>
            a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

        if (sameDay(date, today)) return "Today";
        if (sameDay(date, yesterday)) return "Yesterday";
        return date.toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric" });
    }

    function formatTime(isoString) {
        return new Date(isoString).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
    }

    let toastTimer = null;
    function toast(message) {
        let el = document.getElementById("mozart-toast");
        if (!el) {
            el = document.createElement("div");
            el.id = "mozart-toast";
            el.className = "mozart-toast";
            document.body.appendChild(el);
        }
        el.textContent = message;
        el.classList.add("mozart-toast-visible");
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => el.classList.remove("mozart-toast-visible"), 2200);
    }

    /** Wires up the small "Bridge OK / Bridge error" line most pages show near the top, if present. */
    function reportBridgeStatus(elementId) {
        const el = document.getElementById(elementId);
        if (!el) return;
        MozartBridge.call("system.ping")
            .then((r) => { el.textContent = `Mozart Browser ${r.version}`; })
            .catch((err) => { el.textContent = `Bridge error: ${err.message}`; });
    }

    global.MozartPage = { escapeHtml, formatBytes, formatDayLabel, formatTime, toast, reportBridgeStatus };
})(window);
