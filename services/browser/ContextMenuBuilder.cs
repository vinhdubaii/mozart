using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Builds Mozart's fully custom context menu (guide Part 2). Suppresses
    /// WebView2's default menu entirely and composes the replacement from
    /// independent condition groups - Link/Image/Media/Selection/Editable/
    /// Page/Extension/Trailing - evaluated in a fixed order, exactly like
    /// section 2.4's pseudocode, rather than one big switch on Kind. This
    /// keeps combinations (e.g. an &lt;img&gt; inside an &lt;a&gt;, which is
    /// Kind == Image *and* HasLinkUri == true at once) working for free -
    /// each group just runs its own independent check.
    /// </summary>
    public static class ContextMenuBuilder
    {
        private static readonly Regex ChromeWebStorePattern = new(
            @"^https://chromewebstore\.google\.com/detail/[^/]+/([a-z]{32})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex EdgeAddonsPattern = new(
            @"^https://microsoftedge\.microsoft\.com/addons/detail/[^/]+/([a-zA-Z0-9]{32})",
            RegexOptions.Compiled);

        /// <summary>
        /// Composes and renders the menu for one ContextMenuRequested event.
        /// The only genuinely async step is reading the live loop/controls
        /// state for Video/Audio targets (see QueryMediaStateAsync) - that
        /// state isn't exposed on ContextMenuTarget itself (verified against
        /// the WebView2 API spec: no HasAudio/CanLoop/
        /// ShouldDisplayLoopingControls members actually exist on it, unlike
        /// what the original guide draft assumed), so it has to be queried
        /// from the live element via injected JS before the menu can show
        /// the right checked/enabled state. The caller takes a Deferral to
        /// cover exactly this await.
        /// </summary>
        public static async Task<ContextMenu> BuildMenuAsync(
            ContextMenuHost host, CoreWebView2ContextMenuTarget target, Point location)
        {
            try
            {
                return await ComposeMenuAsync(host, target, location);
            }
            catch
            {
                // Building the menu itself failing (rather than one item's
                // action failing after a click - see ContextMenuActionHandler.
                // RunSafe for that) shouldn't take the whole right-click down
                // with a crash dialog. Fall back to just the always-safe
                // Page + Trailing groups rather than showing nothing at all.
                return Render(new List<ContextMenuGroup> { BuildPageGroup(host), BuildTrailingGroup(host) });
            }
        }

        private static async Task<ContextMenu> ComposeMenuAsync(
            ContextMenuHost host, CoreWebView2ContextMenuTarget target, Point location)
        {
            var groups = new List<ContextMenuGroup>();

            var isVideo = target.Kind == CoreWebView2ContextMenuTargetKind.Video;
            var isAudio = target.Kind == CoreWebView2ContextMenuTargetKind.Audio;
            var isMedia = isVideo || isAudio;

            var mediaState = isMedia
                ? await QueryMediaStateAsync(host, location)
                : (loop: false, controls: false);

            if (target.HasLinkUri)
                groups.Add(BuildLinkGroup(host, target));

            if (target.Kind == CoreWebView2ContextMenuTargetKind.Image)
                groups.Add(BuildImageGroup(host, target, location));

            if (isMedia)
                groups.Add(BuildMediaGroup(host, target, location, isVideo, mediaState));

            // Selection inside an editable field is handled by the Editable
            // group's own Copy item instead (guide 2.5), so it's excluded
            // here to avoid a duplicate Copy entry.
            if (target.HasSelection && !target.IsEditable)
                groups.Add(BuildSelectionGroup(host, target));

            if (target.IsEditable)
                groups.Add(BuildEditableGroup(host));

            // Fallback: plain right-click on page background - only when
            // nothing more specific above matched.
            if (groups.Count == 0)
                groups.Add(BuildPageGroup(host));

            if (IsExtensionStorePage(target.PageUri))
                groups.Add(BuildExtensionGroup(host, target.PageUri));

            groups.Add(BuildTrailingGroup(host));

            return Render(groups);
        }

        // ============================= Group definitions (guide 2.5) =============================

        private static ContextMenuGroup BuildLinkGroup(ContextMenuHost host, CoreWebView2ContextMenuTarget target)
        {
            var linkUri = target.LinkUri;
            return new ContextMenuGroup(
                new ContextMenuEntry
                {
                    Label = "Open link in new window",
                    IconKey = "Icon.OpenLinkNewWindow",
                    OnClick = () => ContextMenuActionHandler.OpenLinkInNewWindow(host, linkUri)
                },
                new ContextMenuEntry
                {
                    Label = "Save link as",
                    IconKey = "Icon.SaveLinkAs",
                    OnClick = () => ContextMenuActionHandler.SaveLinkAs(host, linkUri)
                },
                new ContextMenuEntry
                {
                    Label = "Copy link",
                    IconKey = "Icon.CopyLink",
                    OnClick = () => ContextMenuActionHandler.CopyLink(host, linkUri)
                });
        }

        private static ContextMenuGroup BuildImageGroup(ContextMenuHost host, CoreWebView2ContextMenuTarget target, Point location)
        {
            var sourceUri = target.SourceUri;
            return new ContextMenuGroup(
                new ContextMenuEntry
                {
                    Label = "Save image as",
                    IconKey = "Icon.SaveImageAs",
                    OnClick = () => ContextMenuActionHandler.SaveImageAs(host, sourceUri)
                },
                new ContextMenuEntry
                {
                    Label = "Copy image",
                    IconKey = "Icon.CopyImage",
                    OnClick = () => ContextMenuActionHandler.CopyImage(host, location)
                },
                new ContextMenuEntry
                {
                    Label = "Copy image link",
                    IconKey = "Icon.CopyLink",
                    OnClick = () => ContextMenuActionHandler.CopyImageLink(host, sourceUri)
                });

            // "Magnify image" intentionally excluded - Edge-specific zoom
            // feature, not deemed necessary for Mozart (guide 2.5 note). The
            // icon is still in ContextMenuIcons.xaml (Icon.MagnifyImage) in
            // reserve if that changes.
        }

        private static ContextMenuGroup BuildMediaGroup(
            ContextMenuHost host, CoreWebView2ContextMenuTarget target, Point location,
            bool isVideo, (bool loop, bool controls) mediaState)
        {
            var sourceUri = target.SourceUri;
            var group = new ContextMenuGroup(
                new ContextMenuEntry
                {
                    Label = "Loop",
                    IconKey = "Icon.Loop",
                    IsCheckable = true,
                    IsChecked = mediaState.loop,
                    OnClick = () => ContextMenuActionHandler.ToggleLoop(host, location, !mediaState.loop)
                },
                new ContextMenuEntry
                {
                    Label = "Show all controls",
                    IconKey = "Icon.ShowAllControls",
                    IsEnabled = !mediaState.controls,
                    OnClick = () => ContextMenuActionHandler.ShowAllControls(host, location)
                },
                new ContextMenuEntry
                {
                    Label = isVideo ? "Save video as" : "Save audio as",
                    IconKey = "Icon.SaveVideoAs",
                    OnClick = () => ContextMenuActionHandler.SaveVideoAs(host, sourceUri)
                });

            if (isVideo)
            {
                group.Entries.Add(new ContextMenuEntry
                {
                    Label = "Save video frame as",
                    IconKey = "Icon.SaveVideoFrameAs",
                    OnClick = () => ContextMenuActionHandler.SaveVideoFrameAs(host, location)
                });
                group.Entries.Add(new ContextMenuEntry
                {
                    Label = "Copy video frame",
                    IconKey = "Icon.CopyVideoFrame",
                    OnClick = () => ContextMenuActionHandler.CopyVideoFrame(host, location)
                });
                group.Entries.Add(new ContextMenuEntry
                {
                    Label = "Picture in picture",
                    IconKey = "Icon.PictureInPicture",
                    OnClick = () => ContextMenuActionHandler.TogglePictureInPicture(host, location)
                });
            }

            return group;
        }

        private static ContextMenuGroup BuildSelectionGroup(ContextMenuHost host, CoreWebView2ContextMenuTarget target)
        {
            var selectionText = target.SelectionText;
            return new ContextMenuGroup(
                new ContextMenuEntry
                {
                    Label = "Copy",
                    IconKey = "Icon.CopyImage",
                    OnClick = () => ContextMenuActionHandler.CopySelection(host)
                },
                new ContextMenuEntry
                {
                    Label = $"Search with {App.SearchEngines.DefaultEngine.Name}",
                    IconKey = "Icon.SearchWith",
                    OnClick = () => ContextMenuActionHandler.SearchWith(host, selectionText)
                });
        }

        private static ContextMenuGroup BuildEditableGroup(ContextMenuHost host) =>
            new(
                new ContextMenuEntry
                {
                    Label = "Cut", IconKey = "Icon.Cut",
                    OnClick = () => ContextMenuActionHandler.Cut(host)
                },
                new ContextMenuEntry
                {
                    Label = "Copy", IconKey = "Icon.CopyImage",
                    OnClick = () => ContextMenuActionHandler.CopySelection(host)
                },
                new ContextMenuEntry
                {
                    Label = "Paste", IconKey = "Icon.Paste",
                    OnClick = () => ContextMenuActionHandler.Paste(host)
                },
                new ContextMenuEntry
                {
                    Label = "Select all", IconKey = "Icon.SelectAll",
                    OnClick = () => ContextMenuActionHandler.SelectAll(host)
                });

        private static ContextMenuGroup BuildPageGroup(ContextMenuHost host) =>
            new(
                new ContextMenuEntry
                {
                    Label = "Back", IconKey = "Icon.Back",
                    IsEnabled = host.CoreWebView2.CanGoBack,
                    OnClick = () => ContextMenuActionHandler.Back(host)
                },
                new ContextMenuEntry
                {
                    Label = "Forward", IconKey = "Icon.Forward",
                    IsEnabled = host.CoreWebView2.CanGoForward,
                    OnClick = () => ContextMenuActionHandler.Forward(host)
                },
                new ContextMenuEntry
                {
                    Label = "Refresh", IconKey = "Icon.Refresh",
                    OnClick = () => ContextMenuActionHandler.Refresh(host)
                },
                new ContextMenuEntry
                {
                    Label = "Save as", IconKey = "Icon.SaveAs",
                    OnClick = () => ContextMenuActionHandler.SaveAs(host)
                },
                new ContextMenuEntry
                {
                    Label = "Print", IconKey = "Icon.Print",
                    OnClick = () => ContextMenuActionHandler.Print(host)
                },
                new ContextMenuEntry
                {
                    Label = "View page source", IconKey = "Icon.ViewPageSource",
                    OnClick = () => ContextMenuActionHandler.ViewPageSource(host)
                });

        private static ContextMenuGroup BuildExtensionGroup(ContextMenuHost host, string pageUri) =>
            new(new ContextMenuEntry
            {
                Label = "Add to Mozart",
                IconKey = "Icon.AddToMozart",
                OnClick = () => ContextMenuActionHandler.AddToMozart(host, pageUri)
            });

        private static ContextMenuGroup BuildTrailingGroup(ContextMenuHost host) =>
            new(new ContextMenuEntry
            {
                Label = "Inspect",
                IconKey = "Icon.Inspect",
                OnClick = () => ContextMenuActionHandler.Inspect(host)
            });
        // Share intentionally omitted - deferred, not yet decided whether
        // Mozart will implement OS-level share (guide 2.5). Icon.Share is
        // still in the resource dictionary in reserve.

        // ============================= Extension store detection (guide 1.4) =============================

        private static bool IsExtensionStorePage(string? pageUri)
        {
            if (string.IsNullOrEmpty(pageUri))
                return false;

            return ChromeWebStorePattern.IsMatch(pageUri) || EdgeAddonsPattern.IsMatch(pageUri);
        }

        // ============================= Media state probe =============================

        private static async Task<(bool loop, bool controls)> QueryMediaStateAsync(ContextMenuHost host, Point location)
        {
            var zoom = host.WebView.ZoomFactor;
            if (zoom <= 0) zoom = 1.0;
            var x = location.X / zoom;
            var y = location.Y / zoom;

            var js = $$"""
                (function() {
                    var el = document.elementFromPoint({{x}}, {{y}});
                    while (el && el.tagName !== 'VIDEO' && el.tagName !== 'AUDIO') el = el.parentElement;
                    if (!el) return JSON.stringify({ loop: false, controls: false });
                    return JSON.stringify({ loop: !!el.loop, controls: !!el.controls });
                })();
                """;
            try
            {
                var resultJson = await host.CoreWebView2.ExecuteScriptAsync(js);
                var raw = JsonSerializer.Deserialize<string>(resultJson);
                if (string.IsNullOrEmpty(raw))
                    return (false, false);

                using var doc = JsonDocument.Parse(raw);
                return (
                    doc.RootElement.GetProperty("loop").GetBoolean(),
                    doc.RootElement.GetProperty("controls").GetBoolean());
            }
            catch
            {
                // Best-effort - if the page's JS environment is uncooperative
                // (CSP, a closed shadow DOM around the player, etc.) fall
                // back to unchecked/enabled defaults rather than failing the
                // whole menu over one item's initial state.
                return (false, false);
            }
        }

        // ============================= Rendering (guide 2.6) =============================

        private static ContextMenu Render(List<ContextMenuGroup> groups)
        {
            var menu = new ContextMenu();
            for (var i = 0; i < groups.Count; i++)
            {
                foreach (var entry in groups[i].Entries)
                    menu.Items.Add(RenderEntry(entry));

                if (i < groups.Count - 1)
                    menu.Items.Add(new Separator());
            }
            return menu;
        }

        private static MenuItem RenderEntry(ContextMenuEntry entry)
        {
            var item = new MenuItem
            {
                Header = entry.Label,
                IsCheckable = entry.IsCheckable,
                IsChecked = entry.IsChecked,
                IsEnabled = entry.IsEnabled
            };

            if (entry.IconKey is not null &&
                Application.Current.TryFindResource(entry.IconKey) is DataTemplate iconTemplate)
            {
                // A fresh ContentControl per MenuItem, so each gets its own
                // instantiation of the icon's visual tree from the template
                // instead of trying to share one UIElement instance across
                // menu items (which WPF doesn't allow).
                item.Icon = new ContentControl { ContentTemplate = iconTemplate };
            }

            item.Click += (_, _) => entry.OnClick();
            return item;
        }
    }
}
