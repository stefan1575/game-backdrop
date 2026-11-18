namespace GameBackdrop;

/// <summary>A window's bottom row of buttons: some on the left, some on the right, sized to fit them.</summary>
internal static class ButtonRow
{
    public static Control Create(Control[] left, Control[] right)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(6),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Controls.Add(Flow(left), 0, 0);
        row.Controls.Add(Flow(right), 2, 0);
        return row;
    }

    private static FlowLayoutPanel Flow(Control[] buttons)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty,
        };
        foreach (var button in buttons) button.Margin = new Padding(3);
        flow.Controls.AddRange(buttons);
        return flow;
    }
}
