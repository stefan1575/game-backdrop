using GameBackdrop.Core.Settings;
using GameBackdrop.Windows;

namespace GameBackdrop;

/// <summary>
/// Pick a game from the windows open now: each with its icon, title and exe. Programs already on
/// the list aren't offered.
/// </summary>
internal sealed class OpenWindowPicker : Form
{
    private readonly GameListModel _model;
    private readonly ExeIcons _icons = new(32);
    private readonly ImageList _images = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly ListView _list;
    private readonly Label _empty;
    private readonly Button _add;

    public OpenWindowPicker(GameListModel model)
    {
        _model = model;
        Text = "Add from open windows";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 400);
        MinimumSize = new Size(420, 240);
        MinimizeBox = false;
        ShowInTaskbar = false;

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = _images,
        };
        _list.Columns.Add("Window", 300);
        _list.Columns.Add("Program", 380);
        _list.ItemActivate += (_, _) => Pick();

        _empty = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false,
            Text = "No other windows are open that aren't on the list already.\nStart the game, then press Refresh.",
        };

        _add = new Button { Text = "Add", AutoSize = true, Enabled = false };
        _add.Click += (_, _) => Pick();
        _list.SelectedIndexChanged += (_, _) => _add.Enabled = _list.SelectedItems.Count > 0;
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => Fill();
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = _add;
        CancelButton = cancel;

        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(8) };
        left.Controls.Add(refresh);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(8) };
        right.Controls.AddRange([_add, cancel]);
        var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true };
        bottom.Controls.Add(left);
        bottom.Controls.Add(right);

        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(bottom);
    }

    /// <summary>The window picked, once the dialog returns OK.</summary>
    public OpenWindow? Picked { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Its size is set once the dialog knows its monitor's DPI; setting it empties the list.
        _images.ImageSize = new Size(LogicalToDeviceUnits(20), LogicalToDeviceUnits(20));
        Fill();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _images.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _images.Images.Clear();
        foreach (var window in OpenWindows.List())
        {
            if (_model.Games.Any(g => g.IsFor(window.ExePath))) continue;
            _images.Images.Add(_icons.For(window.ExePath));
            _list.Items.Add(new ListViewItem([window.Title, window.ExePath], _images.Images.Count - 1) { Tag = window });
        }
        _list.EndUpdate();
        var any = _list.Items.Count > 0;
        _list.Visible = any;
        _empty.Visible = !any;
        if (any) _list.Items[0].Selected = true;
        _add.Enabled = any;
    }

    private void Pick()
    {
        if (_list.SelectedItems.Count == 0) return;
        Picked = (OpenWindow)_list.SelectedItems[0].Tag!;
        DialogResult = DialogResult.OK;
    }
}
