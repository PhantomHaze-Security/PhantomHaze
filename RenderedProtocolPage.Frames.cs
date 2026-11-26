namespace PhantomHaze;

internal sealed partial class RenderedWebProtocolPage
{
    private readonly Dictionary<int, (PageFrame Frame, RenderedWebProtocolPage Page)> _frames = [];
    private bool _isEmbeddedFrame;

    private void UpdateFrames(IReadOnlyList<PageFrame> frames)
    {
        // A rerender of the parent keeps a matching child and its scroll position.
        foreach (var (id, child) in _frames.ToArray())
        {
            if (frames.Contains(child.Frame)) continue;
            _frames.Remove(id);
            child.Page.Dispose();
        }

        foreach (PageFrame frame in frames)
        {
            if (_frames.ContainsKey(frame.NodeId)) continue;
            var child = new RenderedWebProtocolPage(Protocol, Host, _transport, scriptsEnabled: false)
            {
                _isEmbeddedFrame = true,
                _rules = _rules,
            };
            child.View.Dock = DockStyle.None;
            child.View.AccessibleName = frame.Title;
            _frames.Add(frame.NodeId, (frame, child));
            _panel.Controls.Add(child.View);
            PositionFrame(frame, child, _panel.AutoScrollPosition);
            _ = child.LoadFrameAsync(frame, _documentResources);
        }
    }

    private void ClearFrames()
    {
        foreach (var child in _frames.Values.ToArray()) child.Page.Dispose();
        _frames.Clear();
    }

    private void PositionFrames(Point scroll)
    {
        foreach (var (_, child) in _frames) PositionFrame(child.Frame, child.Page, scroll);
    }

    private void PositionFrame(PageFrame frame, RenderedWebProtocolPage child, Point scroll)
    {
        if (_panel.GetElementRectangle(frame.ElementId) is not RectangleF rect || rect.Width < 2 || rect.Height < 2)
        {
            child.View.Visible = false;
            return;
        }

        var bounds = Rectangle.Round(rect);
        bounds.Offset(scroll);
        child.View.Bounds = bounds;
        child.View.Visible = true;
    }

    private async Task LoadFrameAsync(PageFrame frame, IPageResourceFetcher resources)
    {
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        string url = frame.Address;
        string? html = frame.SrcDoc;
        try
        {
            if (html is null)
            {
                // Fetch through the selected protocol, with no credentials. The parent keeps
                // a link for embeds requiring another origin, authentication or scripting.
                if (_transport is InspectorTransport)
                {
                    ShowUnavailableFrame();
                    return;
                }

                PageResource? response = await resources.FetchAsync(
                    new PageResourceRequest(url, PageResourceKind.Frame) { MaximumBytes = 256 * 1024 }, token);
                token.ThrowIfCancellationRequested();
                if (response is null || response.Status is < 200 or >= 300 ||
                    !Uri.TryCreate(response.Url, UriKind.Absolute, out var final) ||
                    Uri.Compare(new Uri(url), final, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0 ||
                    !CanDisplayFrame(response))
                {
                    ShowUnavailableFrame();
                    return;
                }

                url = response.Url;
                html = PageText.Decode(response.Body, response.ContentType, isHtml: true);
            }

            await LoadAsync(new PageRequest(url), HistoryUpdate.ReplaceCurrent, _loadSequence, token, html, resources);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsDisposed) ShowUnavailableFrame();
        }
    }

    private static bool CanDisplayFrame(PageResource response)
    {
        string mime = response.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        if (mime is not ("text/html" or "application/xhtml+xml")) return false;
        foreach ((string name, string value) in response.Headers)
        {
            if (name.Equals("X-Frame-Options", StringComparison.OrdinalIgnoreCase) &&
                !value.Trim().Equals("sameorigin", StringComparison.OrdinalIgnoreCase)) return false;
            // Leave ancestor-restricted content to a browser with a full CSP implementation.
            if (name.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void ShowUnavailableFrame() =>
        SetPanelHtml("<html><body>Embedded page preview unavailable.</body></html>   ", keepScrollPosition: false);
}
