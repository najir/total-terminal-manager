// -----------------------------------------------------------------------------
//  Tasks: the JSON store, the table widget and the tab.
// -----------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Terminal.Gui.Input;

namespace Mode;

/// <summary>One task, as stored in tasks.json.</summary>
internal sealed class TaskItem
{
    public const string InProgress = "in-progress";

    public const string Completed = "completed";

    [JsonPropertyName ("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName ("status")]
    public string Status { get; set; } = InProgress;

    [JsonPropertyName ("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName ("creation_date")]
    public string CreationDate { get; set; } = "";

    [JsonPropertyName ("integrations")]
    public JsonObject Integrations { get; set; } = new ();

    [JsonIgnore]
    public bool IsCompleted => string.Equals (Status, Completed, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     The tasks file: %APPDATA%\total-manager\tasks.json, beside user-settings.json and the notes
///     and database folders.
/// </summary>
internal static class TaskStore
{
    private const string FileName = "tasks.json";

    private static readonly JsonSerializerOptions Options = new () { WriteIndented = true };

    public static string FilePath => Path.Combine (UserSettings.FolderPath, FileName);

    public static List<TaskItem> Load ()
    {
        try
        {
            if (!File.Exists (FilePath))
            {
                return new ();
            }

            List<TaskItem> tasks = JsonSerializer.Deserialize<List<TaskItem>> (File.ReadAllText (FilePath))
                                   ?? new List<TaskItem> ();

            foreach (TaskItem task in tasks)
            {
                task.Status = task.IsCompleted ? TaskItem.Completed : TaskItem.InProgress;
            }

            return tasks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ();
        }
    }

    public static bool Save (List<TaskItem> tasks)
    {
        try
        {
            File.WriteAllText (FilePath, JsonSerializer.Serialize (tasks, Options));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
///     The tasks as a table: status, name, description, when it was created - and, when trashable,
///     a delete cell on the right of every row.
/// </summary>
internal sealed class TasksWidget : View
{
    private const int DescriptionWidth = 80;

    private const string StatusOpen = "[ ]";
    private const string StatusDone = "[x]";
    private const string TrashCell = "[del]";

    private const int StatusColumn = 0;
    private const int DescriptionColumn = 2;
    private const int TrashColumn = 4;

    private const string StatusHeader = "";
    private const string TrashHeader = " ";

    private readonly TableView _table;
    private readonly bool _trashable;

    private List<TaskItem> _tasks = new ();

    public TasksWidget (
        bool trashable = false,
        Pos? x = null,
        Pos? y = null,
        Dim? width = null,
        Dim? height = null)
    {
        _trashable = trashable;

        Title = "Tasks";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded;

        this.AddCollapse ();

        X = x ?? Pos.Absolute (0);
        Y = y ?? Pos.Absolute (0);
        Width = width ?? Dim.Auto ();
        Height = height ?? Dim.Auto ();

        _table = new ()
        {
            Width = 1,
            Height = 1,
            FullRowSelect = true,
            MultiSelect = false
        };

        _table.Style.ShowHorizontalHeaderUnderline = true;
        _table.Style.ShowVerticalCellLines = true;
        _table.Style.ExpandLastColumn = false;

        _table.MouseEvent += OnTableMouse;

        Add (_table);

        this.OnShown (Reload);
    }

    public IReadOnlyList<TaskItem> Tasks => _tasks;

    public void Reload ()
    {
        _tasks = TaskStore.Load ();
        Render ();
    }

    private void OnTableMouse (object? sender, Mouse mouse)
    {
        if (!mouse.IsSingleClicked)
        {
            return;
        }

        if (mouse.Position is not { } position)
        {
            return;
        }

        if (_table.ScreenToCell (position.X, position.Y) is not { } cell)
        {
            return;
        }

        if (cell.Y < 0 || cell.Y >= _tasks.Count)
        {
            return;
        }

        if (cell.X == StatusColumn)
        {
            TaskItem task = _tasks [cell.Y];
            task.Status = task.IsCompleted ? TaskItem.InProgress : TaskItem.Completed;

            Commit ();
            mouse.Handled = true;
        }
        else if (cell.X == DescriptionColumn)
        {
            ShowDetails (cell.Y);
            mouse.Handled = true;
        }
        else if (_trashable && cell.X == TrashColumn)
        {
            Delete (cell.Y);

            mouse.Handled = true;
        }
    }

    private void ShowDetails (int row)
    {
        TaskItem task = _tasks [row];

        string created = DateTime.TryParse (task.CreationDate, out DateTime when)
                             ? when.ToString ("yyyy-MM-dd HH:mm")
                             : task.CreationDate;

        string description = task.Description.Length > 0 ? task.Description : "(no description)";

        string details = string.Join (
                                      Environment.NewLine,
                                      task.Name,
                                      "",
                                      "Status    " + task.Status,
                                      "Created   " + created,
                                      "",
                                      description);

        MessageBox.Query (App!, "Task", details, new [] { "_Ok" });
    }

    private void Delete (int row)
    {
        TaskItem task = _tasks [row];

        int? choice = MessageBox.Query (
                                        App!,
                                        "Delete task",
                                        $"Delete '{task.Name}'?" + Environment.NewLine + "This cannot be undone.",
                                        new [] { "_Delete", "_Cancel" });

        if (choice != 0)
        {
            return;
        }

        _tasks.RemoveAt (row);
        Commit ();
    }

    private void Commit ()
    {
        TaskStore.Save (_tasks);
        Render ();
    }

    private void Render ()
    {
        Title = $"Tasks ({_tasks.Count})";

        bool empty = _tasks.Count == 0;
        List<TaskItem> rows = empty ? new () { new TaskItem { Name = "no tasks yet" } } : _tasks;

        Dictionary<string, Func<TaskItem, object>> columns = new ()
        {
            [StatusHeader] = t => empty ? "" : t.IsCompleted ? StatusDone : StatusOpen,
            ["Name"] = t => t.Name,
            ["Description"] = t => Shorten (t.Description),
            ["Created"] = t => Created (t.CreationDate)
        };

        if (_trashable)
        {
            columns [TrashHeader] = _ => empty ? "" : TrashCell;
        }

        _table.SetSource (new EnumerableTableSource<TaskItem> (rows, columns));

        _table.Style.GetOrCreateColumnStyle (DescriptionColumn).MaxWidth = DescriptionWidth;

        int wide = columns.Count + 1;
        int index = 0;

        foreach ((string header, Func<TaskItem, object> cell) in columns)
        {
            int longest = rows.Max (r => cell (r).ToString ()!.Length);

            wide += Math.Min (Math.Max (header.Length, longest), _table.Style.GetOrCreateColumnStyle (index++).MaxWidth);
        }

        _table.Width = wide;

        _table.Height = rows.Count + 3;
        _table.Refresh ();

        this.Remeasure ();
        this.ResizeLayout ();
    }

    private static string Shorten (string description) =>
        description.Length <= DescriptionWidth
            ? description
            : description [.. (DescriptionWidth - 1)] + "\u2026";

    private static string Created (string creationDate) =>
        DateTime.TryParse (creationDate, out DateTime created)
            ? created.ToString ("MM-dd")
            : creationDate;
}

/// <summary>
///     The Tasks tab: a data-entry panel, and below it the same widget the dashboard shows -
///     trashable here, where there is room to manage the list, and not there.
/// </summary>
internal sealed class TasksWindow : Window
{
    private const int LabelGap = 1;

    private const int GroupGap = 2;

    public TasksWindow (Pos x, Pos y)
    {
        Title = "Tasks";
        X = x;
        Y = y;
        Width = Dim.Fill ();
        Height = Dim.Fill ();

        View entry = new ()
        {
            Title = "New task",
            BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded,
            X = 0,
            Y = 0,
            Width = Dim.Auto (),
            Height = Dim.Auto (),
            CanFocus = true
        };

        Label nameLabel = new () { Text = "Name", X = 0, Y = 0 };
        TextField name = new () { X = Pos.Right (nameLabel) + LabelGap, Y = 0, Width = 30 };

        Label descriptionLabel = new () { Text = "Description", X = Pos.Right (name) + GroupGap, Y = 0 };
        TextField description = new () { X = Pos.Right (descriptionLabel) + LabelGap, Y = 0, Width = 44 };

        Button add = new () { Text = "_Add task", X = Pos.Right (description) + GroupGap, Y = 0 };

        Label problem = new () { Text = "", X = 0, Y = Pos.Bottom (nameLabel), Width = Dim.Auto () };

        entry.Add (nameLabel, name, descriptionLabel, description, add, problem);

        TasksWidget tasks = new (trashable: true) { X = 0, Y = Pos.Bottom (entry) };

        void AddTask ()
        {
            string taskName = name.Text.Trim ();

            if (taskName.Length == 0)
            {
                problem.Text = "name is required";

                return;
            }

            List<TaskItem> stored = TaskStore.Load ();

            stored.Add (
                        new TaskItem
                        {
                            Name = taskName,
                            Status = TaskItem.InProgress,
                            Description = description.Text.Trim (),

                            CreationDate = DateTime.Now.ToString ("s")
                        });

            if (!TaskStore.Save (stored))
            {
                problem.Text = "could not write " + TaskStore.FilePath;

                return;
            }

            name.Text = "";
            description.Text = "";
            problem.Text = "";

            tasks.Reload ();
        }

        add.Accepted += (_, _) => AddTask ();
        name.Accepted += (_, _) => AddTask ();
        description.Accepted += (_, _) => AddTask ();

        Add (entry, tasks);
    }
}

