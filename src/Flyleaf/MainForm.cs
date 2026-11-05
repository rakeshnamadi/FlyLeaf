using System.Text;
using Flyleaf.Core;

namespace Flyleaf;

/// <summary>The main window: the list of noted items on the left and the note editor on the right.</summary>
internal sealed class MainForm : Form
{
    private const string MissingLabel = "path missing";

    private readonly NotesRepository _repository = new(NotesRepository.DefaultFilePath);
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        ShowItemToolTips = true,
    };
    private readonly Button _addFileButton = new() { Text = "Browse file…", AutoSize = true };
    private readonly Button _addFolderButton = new() { Text = "Browse folder…", AutoSize = true };
    private readonly Button _removeButton = new() { Text = "Remove", AutoSize = true, Enabled = false };
    private readonly Label _pathLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Drop a file or folder here, or use Browse.",
    };
    private readonly TextBox _editor = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        Enabled = false,
    };
    private readonly Button _saveButton = new() { Text = "Save", AutoSize = true, Enabled = false };
    private readonly Button _listButton = new() { Text = "• List", AutoSize = true, Enabled = false };
    private readonly Label _statusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(6, 0, 0, 0) };
    private readonly TextBox _history = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        TabStop = false,
    };

    private NotesFile _notes = new();
    private Task _loaded = Task.CompletedTask;
    private NoteEntry? _current;
    private bool _changingSelection;
    private bool _allowClose;

    /// <summary>Builds the window.</summary>
    /// <param name="icon">The window icon.</param>
    public MainForm(Icon icon)
    {
        Text = "Flyleaf — A note on every file";
        Icon = icon;
        ClientSize = new Size(960, 600);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? DefaultFont;
        KeyPreview = true;

        BuildLayout();
        WireEvents();
    }

    /// <summary>Opens the note for a file or folder, adding it to the list if needed, ready for typing.</summary>
    /// <param name="path">The full path of the file or folder.</param>
    /// <returns>A task that completes when the note is open.</returns>
    public async Task OpenPathAsync(string path)
    {
        await _loaded;
        await AddPathsAsync([path]);
    }

    /// <summary>Lets the user deal with unsaved changes before the app exits.</summary>
    /// <returns><see langword="false"/> if the user chose to keep working.</returns>
    public async Task<bool> PrepareToExitAsync()
    {
        if (!await ConfirmLeavingCurrentNoteAsync())
        {
            return false;
        }

        _allowClose = true;
        return true;
    }

    private void BuildLayout()
    {
        _list.Columns.Add("File or folder");
        _list.Columns.Add("Last modified");
        _list.Columns.Add("Status");
        _list.ClientSizeChanged += (_, _) => FitListColumns();

        var listButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        listButtons.Controls.AddRange([_addFileButton, _addFolderButton, _removeButton]);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(_list, 0, 0);
        left.Controls.Add(listButtons, 0, 1);

        var saveRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        saveRow.Controls.AddRange([_saveButton, _listButton, _statusLabel]);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        _pathLabel.Height = 28;
        right.Controls.Add(_pathLabel, 0, 0);
        right.Controls.Add(new Label { Text = "Note (shown as the Explorer tooltip)", AutoSize = true }, 0, 1);
        right.Controls.Add(_editor, 0, 2);
        right.Controls.Add(saveRow, 0, 3);
        right.Controls.Add(new Label { Text = "History", AutoSize = true }, 0, 4);
        right.Controls.Add(_history, 0, 5);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);

        // The divider can only be positioned once the container has its final size.
        Load += (_, _) => split.SplitterDistance = (int)(ClientSize.Width * 0.5);
    }

    // The path takes whatever room is left, so "path missing" stays readable at any width or DPI.
    private void FitListColumns()
    {
        var modified = LogicalToDeviceUnits(125);
        var status = LogicalToDeviceUnits(90);
        _list.Columns[1].Width = modified;
        _list.Columns[2].Width = status;
        _list.Columns[0].Width = Math.Max(LogicalToDeviceUnits(80), _list.ClientSize.Width - modified - status);
    }

    private void WireEvents()
    {
        Load += async (_, _) =>
        {
            _loaded = LoadNotesAsync();
            await _loaded;
        };
        Activated += async (_, _) => await RefreshMissingPathsAsync();
        FormClosing += OnFormClosing;
        KeyDown += async (_, e) =>
        {
            if (e is { Control: true, KeyCode: Keys.S } && _saveButton.Enabled)
            {
                e.SuppressKeyPress = true;
                await SaveCurrentAsync();
            }
        };

        _list.SelectedIndexChanged += async (_, _) => await OnSelectionChangedAsync();
        _addFileButton.Click += async (_, _) => await BrowseFileAsync();
        _addFolderButton.Click += async (_, _) => await BrowseFolderAsync();
        _removeButton.Click += async (_, _) => await RemoveCurrentAsync();
        _saveButton.Click += async (_, _) => await SaveCurrentAsync();
        _editor.TextChanged += (_, _) => UpdateDirtyState();
        _editor.KeyDown += OnEditorKeyDown;
        _listButton.Click += (_, _) => ToggleListOnSelectedLines();

        EnableDrop(this);
    }

    private void ToggleListOnSelectedLines()
    {
        var text = _editor.Text;
        var selectionEnd = _editor.SelectionStart + _editor.SelectionLength;
        var start = LineStart(text, _editor.SelectionStart);
        var end = LineEnd(text, selectionEnd);

        _editor.Select(start, end - start);
        _editor.SelectedText = NoteListText.ToggleBullets(text[start..end]);
        _editor.Focus();
    }

    // Enter continues a list. Enter on an empty item ends the list, as in a word processor.
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.None || _editor.SelectionLength > 0)
        {
            return;
        }

        var text = _editor.Text;
        var start = LineStart(text, _editor.SelectionStart);
        var line = text[start.._editor.SelectionStart];
        if (!NoteListText.IsBullet(line) && !NoteListText.IsEmptyBullet(line))
        {
            return;
        }

        e.SuppressKeyPress = true;
        if (NoteListText.IsEmptyBullet(line))
        {
            _editor.Select(start, line.Length);
            _editor.SelectedText = "";
        }
        else
        {
            _editor.SelectedText = "\r\n" + NoteListText.Bullet;
        }
    }

    private static int LineStart(string text, int position) =>
        position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;

    private static int LineEnd(string text, int position)
    {
        var end = text.IndexOf('\r', position);
        return end < 0 ? text.Length : end;
    }

    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Link : DragDropEffects.None;
        control.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            {
                await AddPathsAsync(paths);
            }
        };

        foreach (Control child in control.Controls)
        {
            EnableDrop(child);
        }
    }

    private async Task LoadNotesAsync()
    {
        try
        {
            _notes = await _repository.LoadAsync();
        }
        catch (NotesFileCorruptException)
        {
            RecoverFromCorruptFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Your notes could not be read:\n\n{ex.Message}");
        }

        RebuildList(_notes.Entries.MaxBy(entry => entry.LastModified)?.Path);
        await RefreshMissingPathsAsync();
    }

    private void RecoverFromCorruptFile()
    {
        try
        {
            var backup = _repository.QuarantineCorruptFile();
            ShowError($"Your notes file could not be read, so Flyleaf is starting with an empty list.\n\nThe damaged file was kept here:\n{backup}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Your notes file could not be read or moved aside:\n\n{ex.Message}");
        }
    }

    private void RebuildList(string? selectPath)
    {
        _changingSelection = true;
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var entry in _notes.Entries.OrderByDescending(entry => entry.LastModified))
        {
            var item = new ListViewItem([entry.Path, entry.LastModified.ToLocalTime().ToString("g"), ""])
            {
                Tag = entry,
                ToolTipText = entry.Path,
                UseItemStyleForSubItems = true,
            };
            _list.Items.Add(item);

            if (selectPath is not null && entry.Path.Equals(selectPath, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
                item.EnsureVisible();
            }
        }

        _list.EndUpdate();
        _changingSelection = false;

        ShowEntry(_list.SelectedItems.Count > 0 ? (NoteEntry)_list.SelectedItems[0].Tag! : null);
    }

    private async Task RefreshMissingPathsAsync()
    {
        var entries = _notes.Entries.ToArray();

        // Checking a path can stall on a disconnected network drive, so never do it on the UI thread.
        var missing = await Task.Run(() => entries
            .Where(entry => !File.Exists(entry.Path) && !Directory.Exists(entry.Path))
            .ToHashSet());

        if (IsDisposed)
        {
            return;
        }

        foreach (ListViewItem item in _list.Items)
        {
            var isMissing = missing.Contains((NoteEntry)item.Tag!);
            item.ForeColor = isMissing ? Color.Firebrick : SystemColors.WindowText;
            item.SubItems[2].Text = isMissing ? MissingLabel : "";
        }
    }

    private async Task OnSelectionChangedAsync()
    {
        if (_changingSelection || _list.SelectedItems.Count == 0)
        {
            return;
        }

        var selected = (NoteEntry)_list.SelectedItems[0].Tag!;
        if (selected == _current)
        {
            return;
        }

        if (!await ConfirmLeavingCurrentNoteAsync())
        {
            SelectEntryInList(_current);
            return;
        }

        ShowEntry(selected);
    }

    private void SelectEntryInList(NoteEntry? entry)
    {
        _changingSelection = true;
        foreach (ListViewItem item in _list.Items)
        {
            item.Selected = item.Tag == entry;
        }

        _changingSelection = false;
    }

    private void ShowEntry(NoteEntry? entry)
    {
        _current = entry;
        _editor.Enabled = entry is not null;
        _listButton.Enabled = entry is not null;
        _removeButton.Enabled = entry is not null;
        _pathLabel.Text = entry?.Path ?? "Drop a file or folder here, or use Browse.";
        _editor.Text = entry?.Description.ReplaceLineEndings("\r\n") ?? "";
        _history.Text = entry is null ? "" : FormatHistory(entry);
        _statusLabel.Text = "";
        UpdateDirtyState();
    }

    private static string FormatHistory(NoteEntry entry)
    {
        if (entry.History.Count == 0)
        {
            return "No earlier notes.";
        }

        var text = new StringBuilder();
        foreach (var item in entry.History)
        {
            text.Append(item.Timestamp.ToLocalTime().ToString("g")).Append("\r\n");
            text.Append(item.Text.ReplaceLineEndings("\r\n")).Append("\r\n\r\n");
        }

        return text.ToString();
    }

    private bool HasUnsavedChanges =>
        _current is not null && _editor.Text.ReplaceLineEndings("\n") != _current.Description;

    private void UpdateDirtyState()
    {
        _saveButton.Enabled = HasUnsavedChanges;
        if (HasUnsavedChanges)
        {
            _statusLabel.Text = "Unsaved changes";
        }
    }

    /// <summary>Asks what to do with unsaved edits.</summary>
    /// <returns><see langword="false"/> if the user wants to stay on the current note.</returns>
    private async Task<bool> ConfirmLeavingCurrentNoteAsync()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            $"Save your changes to the note on\n{_current!.Path}?",
            "Flyleaf",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Cancel)
        {
            return false;
        }

        if (answer == DialogResult.No)
        {
            // Put the saved text back, so the same edits aren't asked about again.
            _editor.Text = _current.Description.ReplaceLineEndings("\r\n");
            return true;
        }

        NotesFile.UpdateDescription(_current, _editor.Text, DateTimeOffset.Now);
        if (!await TrySaveAsync())
        {
            return false;
        }

        UpdateListItem(_current);
        return true;
    }

    private async Task SaveCurrentAsync()
    {
        if (_current is null)
        {
            return;
        }

        var entry = _current;
        NotesFile.UpdateDescription(entry, _editor.Text, DateTimeOffset.Now);
        if (await TrySaveAsync())
        {
            _history.Text = FormatHistory(entry);
            UpdateListItem(entry);
            UpdateDirtyState();
            _statusLabel.Text = "Saved";

            // Save has just disabled itself, so give focus back to the editor rather than the next control.
            _editor.Focus();
        }
    }

    private void UpdateListItem(NoteEntry? entry)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag == entry)
            {
                item.SubItems[1].Text = entry!.LastModified.ToLocalTime().ToString("g");
            }
        }
    }

    private async Task<bool> TrySaveAsync()
    {
        try
        {
            await _repository.SaveAsync(_notes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Your note could not be saved:\n\n{ex.Message}");
            return false;
        }
    }

    private async Task BrowseFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a file to add a note to",
            Multiselect = true,
            DereferenceLinks = false,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await AddPathsAsync(dialog.FileNames);
        }
    }

    private async Task BrowseFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder to add a note to", UseDescriptionForTitle = true };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await AddPathsAsync([dialog.SelectedPath]);
        }
    }

    private async Task AddPathsAsync(string[] paths)
    {
        if (!await ConfirmLeavingCurrentNoteAsync())
        {
            return;
        }

        NoteEntry? last = null;
        foreach (var path in paths)
        {
            last = _notes.GetOrAdd(path, DateTimeOffset.Now);
        }

        await TrySaveAsync();
        RebuildList(last?.Path);
        await RefreshMissingPathsAsync();
        Activate();
        _editor.Focus();
        _editor.Select(_editor.TextLength, 0);
    }

    private async Task RemoveCurrentAsync()
    {
        if (_current is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Remove the note and its history from\n{_current.Path}?\n\nThe file itself is not touched.",
            "Flyleaf",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _notes.Entries.Remove(_current);
        await TrySaveAsync();
        RebuildList(selectPath: null);
        await RefreshMissingPathsAsync();
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        // Closing the window only hides it. The app keeps running in the tray until Exit.
        e.Cancel = true;
        if (await ConfirmLeavingCurrentNoteAsync())
        {
            Hide();
        }
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Flyleaf", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
