using System.Drawing.Imaging;

namespace PhantomHaze;

public partial class MainForm
{
    private sealed record Favorite(string Url, string Title, string? Icon);

    private readonly ToolStrip _favoritesBar = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, CanOverflow = true, GripStyle = ToolStripGripStyle.Hidden,
        Margin = Padding.Empty, Padding = Padding.Empty, AccessibleName = "Favourites",
        LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
    };

    private int FindFavorite(string url) => _favorites.FindIndex(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase));

    private static Favorite FavoriteFromTab(BrowserTab tab, string url)
    {
        string title = tab.IsNavigationPending || string.IsNullOrWhiteSpace(tab.Page.Title) ? BuildFallbackTitle(url) : tab.Page.Title;
        string? icon = null;
        if (!tab.IsNavigationPending && tab.OwnedFaviconImage is Image image && tab.FaviconSequence == tab.NavigationSequence)
        {
            using var thumbnail = new Bitmap(image, 16, 16);
            using var bytes = new MemoryStream();
            thumbnail.Save(bytes, ImageFormat.Png);
            icon = Convert.ToBase64String(bytes.ToArray());
        }
        return new Favorite(url, title, icon);
    }

    private void UpdateSavedFavorite(BrowserTab tab)
    {
        if (tab.IsNavigationPending || tab.Page.InnermostPage is RenderedWebProtocolPage { IsInspectorPreview: true } ||
            !TryNormalizeUrl(tab.Page.CurrentUrl, out string url)) return;
        int index = FindFavorite(url);
        if (index < 0) return;
        Favorite favorite = FavoriteFromTab(tab, url);
        favorite = favorite with { Icon = favorite.Icon ?? _favorites[index].Icon };
        if (_favorites[index] == favorite) return;
        _favorites[index] = favorite;
        SaveFavorites();
        RefreshFavoritesBar();
    }

    private static Image ReadFavoriteIcon(string? encoded)
    {
        if (encoded is { Length: > 0 and <= 8192 })
        {
            try
            {
                using var bytes = new MemoryStream(Convert.FromBase64String(encoded));
                using var image = new Bitmap(bytes);
                return new Bitmap(image, 16, 16);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OutOfMemoryException)
            {
                // Old URL-only favourites and unavailable icons use the same fallback as tabs.
            }
        }
        return new Bitmap(DefaultTabIcon, 16, 16);
    }

    private void RefreshFavoritesBar()
    {
        if (_addressRootLayout is null || _favoritesBar.IsDisposed) return;
        _favoritesBar.SuspendLayout();
        try
        {
            while (_favoritesBar.Items.Count > 0) _favoritesBar.Items[0].Dispose();
            foreach (Favorite favorite in _favorites)
            {
                string title = string.IsNullOrWhiteSpace(favorite.Title) ? BuildFallbackTitle(favorite.Url) : favorite.Title;
                string caption = title.Length > 28 ? title[..27] + "…" : title;
                var button = new ToolStripButton(caption.Replace("&", "&&"), ReadFavoriteIcon(favorite.Icon))
                {
                    ToolTipText = title + Environment.NewLine + favorite.Url, AccessibleName = title,
                    TextImageRelation = TextImageRelation.ImageBeforeText, Overflow = ToolStripItemOverflow.AsNeeded,
                };
                var icon = button.Image;
                button.Disposed += (_, _) => icon?.Dispose();
                button.Click += (_, _) =>
                {
                    if (_activeTab is { } tab) NavigateTabToUrl(tab, favorite.Url, "Favourite");
                };
                _favoritesBar.Items.Add(button);
            }
            _favoritesBar.Visible = FavoritesBarHeight > 0;
            _favoritesBar.ImageScalingSize = new Size(ScaleByDpi(16), ScaleByDpi(16));
            _addressRootLayout.RowStyles[2].Height = FavoritesBarHeight;
            UpdateHeaderLayout();
        }
        finally { _favoritesBar.ResumeLayout(true); }
    }
}
