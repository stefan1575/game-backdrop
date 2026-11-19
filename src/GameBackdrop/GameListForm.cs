using System.Diagnostics;
using GameBackdrop.Core.Settings;

namespace GameBackdrop;

/// <summary>
/// The game list window: each game with its icon and name, its two switches, and the buttons to
/// add (by browsing to an exe or from the windows open now), rename and remove. Every change goes
/// through the <see cref="GameListModel"/>, which saves it at once; the grid is then redrawn from
/// the model, so it always shows what the file holds.
/// </summary>
internal sealed class GameListForm : Form
{
    private const int IconColumn = 0, NameColumn = 1, EnabledColumn = 2, KeepMouseColumn = 3, ExeColumn = 4;

    private readonly GameListModel _model;
    private readonly ExeIcons _icons = new(32);
    private readonly DataGridView _grid;
    private readonly Button _rename;
    private readonly Button _remove;
    private bool _loading;
    private bool _reloadQueued;

    public GameListForm(GameListModel model)
    {
        _model = model;
        Text = "Game Backdrop: games";
        Icon = AppIcon.Large;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 420);
        MinimumSize = new Size(560, 260);
        KeyPreview = true;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            StandardTab = true,
        };
        _grid.RowTemplate.Height = 30;
        _grid.Columns.Add(new DataGridViewImageColumn
        {
            HeaderText = "", Width = 36, ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true,
            Resizable = DataGridViewTriState.False,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 220 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Width = 50 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Keep mouse inside", Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Program", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            DefaultCellStyle = { ForeColor = SystemColors.GrayText },
        });
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // A switch takes effect on the click, not when the cell loses focus.
            if (_grid.CurrentCell is DataGridViewCheckBoxCell && _grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += OnCellValueChanged;
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex is IconColumn or ExeColumn) BeginRename();
        };

        var addExe = new Button { Text = "Add program…", AutoSize = true };
        addExe.Click += (_, _) => AddByBrowsing();
        var addOpen = new Button { Text = "Add from open windows…", AutoSize = true };
        addOpen.Click += (_, _) => AddFromOpenWindows();
        _rename = new Button { Text = "Rename", AutoSize = true };
        _rename.Click += (_, _) => BeginRename();
        _remove = new Button { Text = "Remove", AutoSize = true };
        _remove.Click += (_, _) => RemoveSelected();
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;

        Controls.Add(_grid);
        Controls.Add(ButtonRow.Create([addExe, addOpen, _rename, _remove], [close]));

        _model.Changed += QueueReload;
        Reload();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete && !_grid.IsCurrentCellInEditMode && SelectedExe() is not null)
        {
            RemoveSelected();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _model.Changed -= QueueReload;
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Redraw after the change that raised Changed has returned: the grid can't take new rows from
    /// inside its own cell event.
    /// </summary>
    private void QueueReload()
    {
        if (_reloadQueued || !IsHandleCreated) return;
        _reloadQueued = true;
        BeginInvoke(Reload);
    }

    /// <summary>Show the model's list, keeping the selected game (by exe) and column.</summary>
    private void Reload()
    {
        _reloadQueued = false;
        var selected = SelectedExe();
        var column = _grid.CurrentCell?.ColumnIndex ?? NameColumn;
        _loading = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var game in _model.Games)
            {
                var row = _grid.Rows.Add(_icons.For(game.ExePath), game.Name, game.Enabled, game.KeepMouseInside, game.ExePath);
                _grid.Rows[row].Tag = game.ExePath;
            }
            Select(selected, column);
        }
        finally
        {
            _loading = false;
        }
        UpdateButtons();
    }

    private void Select(string? exePath, int column = NameColumn)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (exePath is null || !string.Equals((string)row.Tag!, exePath, StringComparison.OrdinalIgnoreCase)) continue;
            _grid.CurrentCell = row.Cells[column];
            return;
        }
    }

    private string? SelectedExe() => _grid.CurrentRow?.Tag as string;

    private void UpdateButtons()
    {
        var any = SelectedExe() is not null;
        _rename.Enabled = any;
        _remove.Enabled = any;
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        var exe = (string)row.Tag!;
        var value = row.Cells[e.ColumnIndex].Value;
        Save(() =>
        {
            switch (e.ColumnIndex)
            {
                case NameColumn: _model.Rename(exe, value as string); break;
                case EnabledColumn: _model.SetEnabled(exe, value is true); break;
                case KeepMouseColumn: _model.SetKeepMouseInside(exe, value is true); break;
            }
        });
        // A blank name shows the exe's name again; a refused change shows what's saved.
        QueueReload();
    }

    private void BeginRename()
    {
        if (_grid.CurrentRow is not { } row) return;
        _grid.CurrentCell = row.Cells[NameColumn];
        _grid.BeginEdit(selectAll: true);
    }

    private void RemoveSelected()
    {
        if (SelectedExe() is not { } exe) return;
        var name = _model.Games.FirstOrDefault(g => g.IsFor(exe))?.Name ?? exe;
        var answer = MessageBox.Show(this, $"Remove {name} from the list?\n\nIf it's running with a backdrop, it's put back as it was.",
            "Game Backdrop", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.OK) Save(() => _model.Remove(exe));
    }

    private void AddByBrowsing()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Add a game",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true,
            DereferenceLinks = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Add(dialog.FileName, DescriptionOf(dialog.FileName));
    }

    private void AddFromOpenWindows()
    {
        using var picker = new OpenWindowPicker(_model);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Picked is not { } window) return;
        Add(window.ExePath, window.Title);
    }

    private void Add(string exePath, string? displayName)
    {
        var result = AddResult.Added;
        if (!Save(() => result = _model.Add(exePath, displayName))) return;
        switch (result)
        {
            case AddResult.AlreadyListed:
                MessageBox.Show(this, $"{exePath}\n\nis already on the list.", "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case AddResult.NotAFullPath:
                MessageBox.Show(this, $"{exePath}\n\nisn't a full path to a program.", "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
        }
        Reload();
        Select(_model.Games.FirstOrDefault(g => g.IsFor(exePath))?.ExePath);
    }

    /// <summary>Make a change through the model; a save that fails is shown, and the list stays as saved. False if it failed.</summary>
    private bool Save(Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (SettingsSaveException e)
        {
            MessageBox.Show(this, e.Message, "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            QueueReload();
            return false;
        }
    }

    /// <summary>The exe's own description ("Dota 2"), as Explorer shows it, or null to use its file name.</summary>
    private static string? DescriptionOf(string exePath)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
