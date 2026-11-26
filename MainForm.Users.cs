namespace PhantomHaze;

public partial class MainForm
{
    private static void AddScrollingSettingsLayout(TabPage page, TableLayoutPanel layout)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        layout.AutoScroll = false;
        layout.Dock = DockStyle.Top;
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        viewport.Controls.Add(layout);
        page.Controls.Add(viewport);
    }

    private TabPage BuildUsersTab(Form owner)
    {
        var page = new TabPage("Users");
        var layout = new TableLayoutPanel { ColumnCount = 1, Padding = ScalePadding(12, 12, 12, 12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var toggle = new CheckBox { Text = "Enable PhantomHaze Users", AutoSize = true };
        var note = new Label { Text = "User changes take effect immediately. Save other settings before switching users.", AutoSize = true };
        var list = new ListBox { Dock = DockStyle.Top, Height = ScaleByDpi(175), AccessibleName = "PhantomHaze users" };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        layout.Controls.Add(toggle);
        layout.Controls.Add(note);
        layout.Controls.Add(list);
        layout.Controls.Add(actions);
        AddScrollingSettingsLayout(page, layout);
        PhantomUsers state = _userStore.Read();
        bool refreshing = false;
        PhantomUser? Selected() => list.SelectedIndex >= 0 ? state.Users[list.SelectedIndex] : null;
        void Commit(PhantomUsers next) => state = _userStore.Save(next, _userLease);
        void Replace(PhantomUser updated) =>
            Commit(state with { Users = state.Users.Select(u => u.Id == updated.Id ? updated : u).ToList() });
        bool Confirm(string message) => ShowCaptureProtectedDialog(() => MessageBox.Show(owner, message, "PhantomHaze Users",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)) == DialogResult.Yes;
        bool Unlock(PhantomUser user)
        {
            if (user.PinHash is null) return true;
            string? pin = PromptUserValue(owner, $"PIN for {user.Name}", "", pin: true);
            if (pin is null) return false;
            if (!user.Accepts(pin)) throw new ArgumentException("The PIN was incorrect.");
            return true;
        }
        void SwitchTo(PhantomUser user)
        {
            NextUser = user;
            owner.DialogResult = DialogResult.Abort;
        }
        void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
            {
                ShowCaptureProtectedDialog(() => MessageBox.Show(owner, ex.Message, "PhantomHaze Users", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
        }
        Button AddButton(string label)
        {
            var button = new Button { Text = label, AutoSize = true };
            actions.Controls.Add(button);
            return button;
        }
        void OnClick(Button button, Action action) => button.Click += (_, _) => Run(action);
        var add = AddButton("Add user");
        var switchButton = AddButton("Switch to");
        var rename = AddButton("Rename");
        var pinButton = AddButton("Set PIN");
        var makeDefault = AddButton("Make default");
        var delete = AddButton("Delete");
        string? ReadName(string title, string initial)
        {
            string? value = PromptUserValue(owner, title, initial, pin: false)?.Trim();
            if (value == "") throw new ArgumentException("Enter a name for this user.");
            if (state.Users.Any(u => u.Name != initial && u.Name.Equals(value, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("That name is already in use.");
            return value;
        }
        OnClick(add, () =>
        {
            if (ReadName("New user name", "") is not string name) return;
            var user = new PhantomUser(Guid.NewGuid(), name);
            Commit(state with { Users = [.. state.Users, user] });
            RefreshList(user.Id);
        });
        OnClick(switchButton, () =>
        {
            if (Selected() is { } user && Unlock(user)) SwitchTo(user);
        });
        OnClick(rename, () =>
        {
            if (Selected() is not { } user || ReadName("Rename user", user.Name) is not string name) return;
            Replace(user with { Name = name });
            RefreshList(user.Id);
        });
        OnClick(pinButton, () =>
        {
            if (Selected() is not { } user || user.Id == state.DefaultId || !Unlock(user)) return;
            string? pin = PromptUserValue(owner, "New four-digit PIN (leave empty to remove)", "", pin: true);
            if (pin is null) return;
            Replace(pin.Length == 0 ? user with { PinHash = null } : user.WithPin(pin));
            RefreshList(user.Id);
        });
        OnClick(makeDefault, () =>
        {
            if (Selected() is not { } user || !Unlock(user)) return;
            Commit(state with { DefaultId = user.Id, Users = state.Users.Select(u => u.Id == user.Id ? u with { PinHash = null } : u).ToList() });
            RefreshList(user.Id);
        });
        OnClick(delete, () =>
        {
            if (Selected() is not { } user || user.Id == state.DefaultId || !Confirm($"Delete {user.Name} and their saved data?")) return;
            Commit(state with { Users = state.Users.Where(u => u.Id != user.Id).ToList() });
            if (user.Id == _userId) SwitchTo(state.Default);
            else RefreshList(_userId);
        });
        void UpdateActions()
        {
            PhantomUser? selected = Selected();
            rename.Enabled = selected is not null;
            switchButton.Enabled = selected is not null && selected.Id != _userId;
            delete.Enabled = makeDefault.Enabled = pinButton.Enabled = selected is not null && selected.Id != state.DefaultId;
            pinButton.Text = selected?.PinHash is null ? "Set PIN" : "Change PIN";
        }
        void RefreshList(Guid selectedId)
        {
            refreshing = true;
            toggle.Checked = state.Enabled;
            actions.Visible = list.Visible = note.Visible = state.Enabled;
            list.Items.Clear();
            foreach (var user in state.Users)
                list.Items.Add(user.Name + (user.Id == state.DefaultId ? " (default)" : "") + (user.Id == _userId ? " (current)" : "") + (user.PinHash is not null ? " · PIN" : ""));
            list.SelectedIndex = Math.Max(0, state.Users.FindIndex(u => u.Id == selectedId));
            UpdateActions();
            refreshing = false;
        }
        list.SelectedIndexChanged += (_, _) => UpdateActions();
        toggle.CheckedChanged += (_, _) =>
        {
            if (refreshing) return;
            Run(() =>
            {
                if (toggle.Checked)
                    Commit(state with { Enabled = true, Users = [state.Default with { Name = "Master" }] });
                else if (state.Users.Count == 1 || Confirm($"Keep {state.Default.Name}'s data and permanently delete all other users and their data?"))
                {
                    Commit(state with { Enabled = false, Users = [state.Default] });
                    if (_userId != state.DefaultId) SwitchTo(state.Default);
                }
            });
            RefreshList(_userId);
        };
        RefreshList(_userId);
        return page;
    }

    private string? PromptUserValue(Form owner, string label, string initial, bool pin)
    {
        using var dialog = new Form
        {
            Text = label, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(ScaleByDpi(340), ScaleByDpi(108)),
        };
        var input = new TextBox { Text = initial, UseSystemPasswordChar = pin, MaxLength = pin ? 4 : 60, AccessibleName = label, Dock = DockStyle.Top };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([cancel, ok]);
        dialog.Padding = ScalePadding(12, 12, 12, 12);
        dialog.Controls.Add(input);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.ActiveControl = input;
        ApplyDialogTheme(dialog);
        RegisterProtectedAuxiliaryWindow(dialog, "Users");
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}
