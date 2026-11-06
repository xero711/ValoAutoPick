namespace ValoAutoPick;

public sealed class MapSettingsDialog : DarkForm
{
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
    private readonly TableLayoutPanel rows = new() { BackColor = Theme.Background, ColumnCount = 2, Margin = Padding.Empty };
    private readonly PickerScrollBar scrollBar = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, AccessibleName = "マップ一覧のスクロール" };
    public Dictionary<string, string> MapAgents { get; private set; }

    public MapSettingsDialog(IReadOnlyList<Agent> agents, Dictionary<string, string> existing)
    {
        Text = "マップ別エージェント設定"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 760); MinimumSize = new Size(500, 460);
        BackColor = Theme.Background; ForeColor = Theme.Text; Font = new Font("Yu Gothic UI", 10);
        MapAgents = new(existing, StringComparer.OrdinalIgnoreCase);
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        rows.RowCount = MapCatalog.Maps.Length + 1;
        var entries = new List<(string Key, ThemedSelect Select)>();
        var options = new[] { "通常の設定を使用" }.Concat(agents.Select(a => a.Name)).ToArray();
        var hint = new Label { Text = "未設定のマップは通常のエージェントを使用します", Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        rows.Controls.Add(hint, 0, 0); rows.SetColumnSpan(hint, 2);
        var row = 1;
        foreach (var (key, name) in MapCatalog.Maps)
        {
            var label = new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var select = new ThemedSelect(options) { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 5) };
            var selected = existing.TryGetValue(key, out var id) ? agents.ToList().FindIndex(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) : -1;
            select.SelectedIndex = selected + 1; entries.Add((key, select));
            rows.Controls.Add(label, 0, row); rows.Controls.Add(select, 1, row++);
            select.Enter += (_, _) => EnsureVisible(select);
        }
        var list = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        viewport.Controls.Add(rows); list.Controls.Add(viewport, 0, 0); list.Controls.Add(scrollBar, 1, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 72, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var save = new ThemedButton { Text = "保存", Width = 130, Height = 48, Primary = true }; var cancel = new ThemedButton { Text = "キャンセル", Width = 130, Height = 48, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => { foreach (var (key, select) in entries) { if (select.SelectedIndex > 0 && select.SelectedIndex <= agents.Count) MapAgents[key] = agents[select.SelectedIndex - 1].Id; else MapAgents.Remove(key); } DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); Controls.Add(list); Controls.Add(buttons); AcceptButton = save; CancelButton = cancel;
        scrollBar.ValueChanged += (_, _) => rows.Top = -scrollBar.Value;
        viewport.SizeChanged += (_, _) => ArrangeRows();
        void AttachWheel(Control control)
        {
            control.MouseWheel += (_, e) =>
            {
                scrollBar.SetValue(scrollBar.Value - e.Delta * UiMetrics.Px(this, 48) / 120 * Math.Max(1, SystemInformation.MouseWheelScrollLines));
                if (e is HandledMouseEventArgs handled) handled.Handled = true;
            };
        }
        AttachWheel(viewport); AttachWheel(rows); AttachWheel(scrollBar);
        foreach (Control control in rows.Controls) AttachWheel(control);
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); ArrangeRows(); }
    private void ArrangeRows()
    {
        if (viewport.ClientSize.Width <= 0 || viewport.ClientSize.Height <= 0) return;
        rows.SuspendLayout();
        var pad = UiMetrics.Px(this, 18);
        var headingHeight = UiMetrics.Px(this, 54);
        var rowHeight = UiMetrics.Px(this, 58);
        rows.Padding = new Padding(pad, 0, pad, pad);
        rows.RowStyles.Clear(); rows.RowStyles.Add(new RowStyle(SizeType.Absolute, headingHeight));
        for (var i = 0; i < MapCatalog.Maps.Length; i++) rows.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        rows.Size = new Size(viewport.ClientSize.Width, headingHeight + rowHeight * MapCatalog.Maps.Length + pad);
        rows.ResumeLayout(true);
        scrollBar.SetRange(rows.Height, viewport.ClientSize.Height);
        rows.Top = -scrollBar.Value;
    }
    internal void PreviewBottom() => scrollBar.SetValue(int.MaxValue);
    private void EnsureVisible(Control control)
    {
        if (control.Top < scrollBar.Value) scrollBar.SetValue(control.Top);
        else if (control.Bottom > scrollBar.Value + viewport.ClientSize.Height) scrollBar.SetValue(control.Bottom - viewport.ClientSize.Height);
    }
}
