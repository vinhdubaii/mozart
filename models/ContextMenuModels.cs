using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MozartBrowser.Models
{
    /// <summary>
    /// One row in Mozart's fully custom context menu (Part 2 of the
    /// extensions-and-context-menu guide). Produced by
    /// ContextMenuBuilder, materialized into a real WPF MenuItem by
    /// the same class - this type only carries data, no WPF objects, so it
    /// stays easy to unit-test independently of any window.
    /// </summary>
    public class ContextMenuEntry
    {
        public required string Label { get; init; }

        /// <summary>
        /// Key into the DataTemplate resources in Themes/ContextMenuIcons.xaml
        /// (e.g. "Icon.CopyLink"), or null to render with no icon.
        /// </summary>
        public string? IconKey { get; init; }

        public bool IsCheckable { get; init; }
        public bool IsChecked { get; init; }
        public bool IsEnabled { get; init; } = true;

        /// <summary>Invoked on click. Wired up by ContextMenuBuilder to a ContextMenuActionHandler method.</summary>
        public required Action OnClick { get; init; }
    }

    /// <summary>
    /// A contiguous run of related ContextMenuEntry items - Link, Image,
    /// Media, Selection, Editable, Page, Extension, or Trailing (see guide
    /// section 2.4/2.5). ContextMenuBuilder.Render inserts a Separator
    /// between groups, but never after the last one.
    /// </summary>
    public class ContextMenuGroup
    {
        public List<ContextMenuEntry> Entries { get; } = new();

        public ContextMenuGroup(params ContextMenuEntry[] entries) => Entries.AddRange(entries);
    }

    /// <summary>
    /// The pieces of "the window this WebView2 lives in" that context menu
    /// actions need (opening a new tab, owning dialogs like Save-As/message
    /// boxes). MainWindow and PrivateWindow each construct one of these
    /// inline when wiring up ContextMenuRequested, so ContextMenuBuilder/
    /// ContextMenuActionHandler never have to depend on either window type
    /// directly - private windows just pass their own private-tab-opening
    /// logic through OpenInNewTab instead.
    /// </summary>
    public class ContextMenuHost
    {
        public required WebView2 WebView { get; init; }
        public required Window OwnerWindow { get; init; }

        /// <summary>Opens a URL in a new tab in the same window.</summary>
        public required Action<string> OpenInNewTab { get; init; }

        public CoreWebView2 CoreWebView2 => WebView.CoreWebView2;
    }
}
