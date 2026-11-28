namespace PhantomHaze;

public partial class MainForm
{
    private readonly Panel _inspector = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly TextBox _htmlEditor = new()
    {
        Dock = DockStyle.Fill, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both,
        MaxLength = 0, AcceptsTab = true, AcceptsReturn = true, AccessibleName = "Website HTML",
    };
    private readonly Button _saveHtml = new() { Text = "Save", Dock = DockStyle.Right, AccessibleName = "Save edited HTML" };
    private BrowserTab? _editingTab;
    private int _inspectorVersion;

    private void BuildInspector()
    {
        _inspector.Height = ScaleByDpi(135);
        _inspector.Padding = ScalePadding(5, 3, 5, 3);
        var toolbar = new Panel { Height = ScaleByDpi(28), Dock = DockStyle.Top };
        toolbar.Controls.Add(new Label { Text = "Inspect HTML", Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft });
        toolbar.Controls.Add(_saveHtml);
        _inspector.Controls.Add(_htmlEditor);
        _inspector.Controls.Add(toolbar);
        Controls.Add(_inspector);
        _htmlEditor.TextChanged += (_, _) =>
        {
            if (_editingTab is not null) _editingTab.InspectorDraft = _htmlEditor.Text;
        };
        _saveHtml.Click += async (_, _) => await SaveInspectorAsync();
    }

    private async void RefreshInspector()
    {
        int version = ++_inspectorVersion;
        _editingTab = null;
        _inspector.Visible = _webViewModeEnabled && _settings.InspectHtml;
        _saveHtml.Enabled = false;
        _htmlEditor.ReadOnly = true;
        _htmlEditor.Clear();
        BrowserTab? tab = _activeTab;
        if (!_inspector.Visible || tab is null || tab.IsClosing || tab.IsNavigationPending ||
            !tab.Page.IsReady || !WebProtocolPage.IsWebAddress(tab.Page.DocumentUrl)) return;
        try
        {
            string? source = tab.InspectorDraft ?? await tab.Page.ReadHtmlAsync();
            if (IsDisposed || version != _inspectorVersion || source is null) return;
            _htmlEditor.Text = source;
            _htmlEditor.ReadOnly = false;
            _saveHtml.Enabled = true;
            _editingTab = tab;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or System.Text.Json.JsonException)
        {
            if (!IsDisposed && version == _inspectorVersion) Log($"Could not read the page HTML: {ex.Message}");
        }
    }

    private async Task SaveInspectorAsync()
    {
        if (_editingTab is not { } tab || tab != _activeTab || tab.IsNavigationPending) return;
        string html = _htmlEditor.Text;
        string url = tab.Page.DocumentUrl;
        var addressTemplate = (tab.Page as TemplateWebProtocolPage)?.AddressTemplate;
        if (!WebProtocolPage.IsWebAddress(url) || ProtocolHostInstance.IsBlocked(url, out _)) return;
        try
        {
            if (tab.Page.InnermostPage is RenderedWebProtocolPage rendered)
            {
                rendered.ShowEditedHtml(url, html, addressTemplate);
                return;
            }

            WebProtocolPage original = tab.Page;
            WebProtocolPage native = original.InnermostPage;
            var preview = new RenderedWebProtocolPage(tab.Protocol, ProtocolHostInstance, new InspectorTransport(native), _settings.RenderedPageScriptsEnabled)
            {
                ReturnToBrowser = request =>
                {
                    RestoreNativePage(tab);
                    native.NavigateRequest(request);
                },
            };
            var page = original is TemplateWebProtocolPage template ? template.WrapPreview(preview) : (WebProtocolPage)preview;
            try { await page.InitializeAsync(); }
            catch { page.Dispose(); throw; }
            if (tab.Page != original || !_tabs.Contains(tab) || tab != _editingTab || tab.Page.DocumentUrl != url)
            {
                page.Dispose();
                return;
            }
            original.View.Visible = false;
            tab.OriginalPage = original;
            tab.Page = page;
            _webViewHostPanel.Controls.Add(page.View);
            AttachPageToTab(tab, page);
            page.View.Visible = _webViewModeEnabled && _activeTab == tab;
            page.View.BringToFront();
            preview.ShowEditedHtml(url, html, addressTemplate);
        }
        catch (Exception ex)
        {
            RestoreNativePage(tab);
            Log($"Could not show edited HTML: {ex.Message}");
            ShowCaptureProtectedDialog(() => MessageBox.Show(this, ex.Message, "Inspect HTML", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
    }

    private void RestoreNativePage(BrowserTab tab)
    {
        if (tab.OriginalPage is not { } original) return;
        WebProtocolPage preview = tab.Page;
        tab.Page = original;
        tab.OriginalPage = null;
        tab.NavigationSequence++;
        tab.IsNavigationPending = false;
        tab.InspectorDraft = null;
        _webViewHostPanel.Controls.Remove(preview.View);
        preview.Dispose();
        original.View.Visible = _webViewModeEnabled && _activeTab == tab;
    }
}
