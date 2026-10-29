using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenTap;
using OpenTap.Plugins.BasicSteps;
using Tap.Plugins.Script;
using Tap.Plugins.Uds;
using Tap.Plugins.Workflow;

namespace OpenTap.WpfEditor;

public partial class MainWindow : Window
{
    private readonly WpfLogListener logListener = new();
    private readonly EditorResultListener resultListener = new();
    private readonly EditorSettings settings = EditorSettings.Load();
    private CancellationTokenSource? runCancellation;
    private TestPlan plan = new();
    private string? currentPath;
    private bool isDirty;
    private IReadOnlyList<ITypeData> availableStepTypes = Array.Empty<ITypeData>();
    private readonly Dictionary<ITypeData, (Button Add, Button AddChild)> stepLibraryButtons = new();
    private readonly Dictionary<ITestStep, TestStepRun> completedStepRuns = new();

    public ObservableCollection<PropertyRow> Properties { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        DataContext = this;
        Log.AddListener(logListener);
        logListener.MessageReceived += LogListener_MessageReceived;
        resultListener.StepCompleted += ResultListener_StepCompleted;
        Loaded += (_, _) => InitializeEditor();
    }

    private async void InitializeEditor()
    {
        UserInput.SetInterface(new WpfUserInputInterface());
        SetPlan(new TestPlan());
        availableStepTypes = DiscoverBuiltInStepTypes();
        RebuildStepLibrary();
        UpdateStatus("Discovering Test Step plugins...");
        ConfigurePluginDirectories(null);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await PluginManager.SearchAsync();
            availableStepTypes = DiscoverAvailableStepTypes();
            RebuildStepLibrary();
            UpdateStatus($"Ready - {availableStepTypes.Count} Test Steps");
            AppendLog($"OpenTAP WPF Editor ready. Discovered {availableStepTypes.Count} Test Steps in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
        }
        catch (Exception ex)
        {
            UpdateStatus("Plugin discovery failed");
            ShowError("Could not discover OpenTAP plugins.", ex);
        }
    }

    private static void ConfigurePluginDirectories(string? planPath)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "Packages", "OpenTAP"),
            Path.Combine(AppContext.BaseDirectory, "Packages"),
            Path.Combine(AppContext.BaseDirectory, "Packages", "Python")
        };

        var repositoryRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (repositoryRoot != null)
            directories.Add(Path.Combine(repositoryRoot, "bin", "Debug"));

        if (planPath != null)
            directories.Add(Path.GetDirectoryName(planPath) ?? AppContext.BaseDirectory);

        foreach (var directory in directories.Where(Directory.Exists))
            PluginManager.DirectoriesToSearch.Add(directory);
    }

    private void SetPlan(TestPlan newPlan)
    {
        plan = newPlan;
        completedStepRuns.Clear();
        currentPath = plan.Path;
        isDirty = false;
        RebuildTree();
        Properties.Clear();
        UpdateTitle();
        UpdateStatus("Ready");
    }

    private void RebuildTree()
    {
        PlanTree.Items.Clear();
        var root = new TreeViewItem { Header = CreatePlanHeader(), Tag = plan, IsExpanded = true };
        foreach (var step in plan.ChildTestSteps)
            root.Items.Add(CreateNode(step));
        PlanTree.Items.Add(root);
        PathText.Text = currentPath ?? "Untitled";
    }

    private Grid CreatePlanHeader()
    {
        var header = new Grid { MinWidth = 560 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.85, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.85, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.1, GridUnitType.Star) });
        AddHeaderCell(header, "Test Plan: " + plan.Name, 1, true);
        AddHeaderCell(header, "-", 2);
        AddHeaderCell(header, "-", 3);
        AddHeaderCell(header, "Plan", 4);
        AddHeaderCell(header, "-", 5);
        AddHeaderCell(header, "-", 6);
        AddHeaderCell(header, "OpenTAP Test Plan", 7);
        return header;
    }

    private TreeViewItem CreateNode(ITestStep step)
    {
        var header = new Grid { MinWidth = 560 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.15, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.85, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.85, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.1, GridUnitType.Star) });
        var enabled = new CheckBox { IsChecked = step.Enabled, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Enable or disable this step" };
        enabled.Checked += (_, _) => { step.Enabled = true; MarkDirty(); };
        enabled.Unchecked += (_, _) => { step.Enabled = false; MarkDirty(); };
        Grid.SetColumn(enabled, 0);
        header.Children.Add(enabled);
        AddHeaderCell(header, step.Name, 1);
        AddVerdictCell(header, step.Verdict, 2);
        AddHeaderCell(header, FormatDurationFromCache(step), 3);
        AddFlowCell(header, step, 4);
        AddHeaderCell(header, FormatMeasuredValue(step), 5);
        AddHeaderCell(header, FormatLimits(step), 6);
        AddHeaderCell(header, step.TypeName, 7);

        var node = new TreeViewItem { Header = header, Tag = step, ContextMenu = CreateStepMenu(step) };
        foreach (var child in step.ChildTestSteps)
            node.Items.Add(CreateNode(child));
        return node;
    }

    private static string FormatVerdict(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "● Pass",
        Verdict.Fail => "● Fail",
        Verdict.Error => "● Error",
        Verdict.Aborted => "● Aborted",
        _ => "○ Not Set"
    };

    private string FormatDurationFromCache(ITestStep step)
    {
        if (!completedStepRuns.TryGetValue(step, out var run))
            return "—";

        var duration = run.Duration;
        if (duration.TotalMilliseconds < 1000)
            return $"{duration.TotalMilliseconds:0.0} ms";
        if (duration.TotalMinutes < 1)
            return $"{duration.TotalSeconds:0.0} s";
        if (duration.TotalHours < 1)
            return $"{duration.TotalMinutes:0.0} min";
        return $"{duration.TotalHours:0.0} h";
    }

    private static string FormatMeasuredValue(ITestStep step)
    {
        var valueProperty = step.GetType().GetProperty("MeasuredValue", BindingFlags.Instance | BindingFlags.Public);
        if (valueProperty == null) return "—";
        var value = valueProperty.GetValue(step) as string;
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var unitProperty = step.GetType().GetProperty("Unit", BindingFlags.Instance | BindingFlags.Public);
        var unit = unitProperty?.GetValue(step) as string;
        return string.IsNullOrWhiteSpace(unit) ? value : value + " " + unit;
    }

    private static string FormatLimits(ITestStep step)
    {
        var decodeProperty = step.GetType().GetProperty("DecodeConfig", BindingFlags.Instance | BindingFlags.Public);
        var decodeText = decodeProperty?.GetValue(step) as string;
        if (!string.IsNullOrWhiteSpace(decodeText))
        {
            try
            {
                var normalized = System.Text.RegularExpressions.Regex.Replace(decodeText.Trim(), "(\\\"[^\\\"]+\\\")\\s*=", "$1:");
                using var document = JsonDocument.Parse(normalized);
                var item = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                    ? document.RootElement[0]
                    : document.RootElement;
                var lower = GetJsonValue(item, "expectedMin", "lowerLimit");
                var upper = GetJsonValue(item, "expectedMax", "upperLimit");
                if (lower != null || upper != null) return (lower ?? "-") + " .. " + (upper ?? "-");
            }
            catch (JsonException) { }
        }

        var lowerProperty = step.GetType().GetProperty("LowerLimit", BindingFlags.Instance | BindingFlags.Public);
        var upperProperty = step.GetType().GetProperty("UpperLimit", BindingFlags.Instance | BindingFlags.Public);
        var lowerLimit = lowerProperty?.GetValue(step)?.ToString();
        var upperLimit = upperProperty?.GetValue(step)?.ToString();
        return string.IsNullOrWhiteSpace(lowerLimit) && string.IsNullOrWhiteSpace(upperLimit)
            ? "—"
            : (string.IsNullOrWhiteSpace(lowerLimit) ? "-" : lowerLimit) + " .. " + (string.IsNullOrWhiteSpace(upperLimit) ? "-" : upperLimit);
    }

    private static string? GetJsonValue(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value)) return value.ToString();
        return null;
    }

    private void AddFlowCell(Grid grid, ITestStep step, int column)
    {
        var flow = new Grid { Width = 96, Height = 10, Margin = new Thickness(4, 5, 8, 5), Background = new SolidColorBrush(Color.FromRgb(224, 229, 235)) };
        var width = completedStepRuns.ContainsKey(step) ? 64 : 0;
        if (width > 0)
            flow.Children.Add(new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(46, 132, 235)) });
        Grid.SetColumn(flow, column);
        grid.Children.Add(flow);
    }

    private static void AddVerdictCell(Grid grid, Verdict verdict, int column)
    {
        var cell = new TextBlock
        {
            Text = FormatVerdict(verdict),
            Foreground = verdict switch
            {
                Verdict.Pass => Brushes.ForestGreen,
                Verdict.Fail => Brushes.IndianRed,
                Verdict.Error => Brushes.DarkRed,
                Verdict.Aborted => Brushes.DarkOrange,
                _ => Brushes.Gray
            },
            FontWeight = verdict is Verdict.Pass or Verdict.Fail or Verdict.Error ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 2, 8, 2)
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private ContextMenu CreateStepMenu(ITestStep step)
    {
        var menu = new ContextMenu();
        var toggle = new MenuItem { Header = step.Enabled ? "Disable" : "Enable" };
        toggle.Click += (_, _) =>
        {
            step.Enabled = !step.Enabled;
            MarkDirty();
            RebuildTree();
        };
        var remove = new MenuItem { Header = "Delete" };
        remove.Click += (_, _) => RemoveStep(step);
        var up = new MenuItem { Header = "Move Up" };
        up.Click += (_, _) => MoveStep(step, -1);
        var down = new MenuItem { Header = "Move Down" };
        down.Click += (_, _) => MoveStep(step, 1);
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }

    private void RemoveStep(ITestStep step)
    {
        if (step.Parent is not ITestStepParent parent || !CanEditPlan())
            return;
        parent.ChildTestSteps.Remove(step);
        MarkDirty();
        Properties.Clear();
        RebuildTree();
        UpdateStatus("Step deleted");
    }

    private void MoveStep(ITestStep step, int delta)
    {
        if (step.Parent is not ITestStepParent parent || !CanEditPlan())
            return;
        var index = parent.ChildTestSteps.IndexOf(step);
        var newIndex = index + delta;
        if (index < 0 || newIndex < 0 || newIndex >= parent.ChildTestSteps.Count)
            return;
        parent.ChildTestSteps.Move(index, newIndex);
        MarkDirty();
        RebuildTree();
    }

    private bool CanEditPlan() => !plan.IsRunning && plan.AllowEdit;

    private static void AddHeaderCell(Grid grid, string text, int column, bool bold = false)
    {
        var cell = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 2, 8, 2), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static void AddActionButton(Grid grid, string text, int column, RoutedEventHandler handler, string tooltip)
    {
        var button = new Button { Content = text, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 1, 2, 1), ToolTip = tooltip };
        button.Click += handler;
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private void PlanTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: ITestStep step })
        {
            LoadProperties(step);
            UpdateStepLibraryButtons();
        }
        else if (e.NewValue is TreeViewItem { Tag: TestPlan selectedPlan })
        {
            LoadProperties(selectedPlan);
            UpdateStepLibraryButtons();
        }
    }

    private void LoadProperties(object? target)
    {
        Properties.Clear();
        if (target == null)
            return;

        var annotations = AnnotationCollection.Annotate(target);
        var members = annotations.Get<IMembersAnnotation>()?.Members ?? Enumerable.Empty<AnnotationCollection>();
        foreach (var memberAnnotation in members)
        {
            var member = memberAnnotation.Get<IMemberAnnotation>()?.Member;
            if (member == null || !member.Readable || !member.Writable)
                continue;
            if (memberAnnotation.Get<IObjectValueAnnotation>() == null)
                continue;
            Properties.Add(new PropertyRow(target, annotations, memberAnnotation, member));
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscard())
            SetPlan(new TestPlan());
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;

        var dialog = new OpenFileDialog { Filter = "OpenTAP Plan (*.TapPlan)|*.TapPlan|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ConfigurePluginDirectories(dialog.FileName);
            await PluginManager.SearchAsync();
            SetPlan(TestPlan.Load(dialog.FileName));
            settings.RememberPlan(dialog.FileName);
            AppendLog($"Loaded plan: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Contains("Unable to locate type", StringComparison.OrdinalIgnoreCase)
                ? "This plan references plugins or test-only types that are not available in the current OpenTAP installation.\n\n" + ex.Message
                : ex.Message;
            ShowError("Could not load the test plan.", new InvalidOperationException(detail, ex));
        }
    }

    private void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;

        var dialog = new OpenFileDialog { Filter = "Excel Workflow (*.xlsx)|*.xlsx", DefaultExt = ".xlsx" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var workflow = ExcelWorkflowCompiler.Compile(dialog.FileName);
            SetPlan(workflow.Plan);
            currentPath = null;
            isDirty = true;
            PathText.Text = dialog.FileName;
            UpdateTitle();
            UpdateStatus($"Imported {workflow.EnabledStepCount} steps");
            AppendLog($"Imported Excel workflow: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError("Could not import the Excel workflow.", ex);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (currentPath == null)
            SaveAs_Click(sender, e);
        else
            SavePlan(currentPath);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "OpenTAP Plan (*.TapPlan)|*.TapPlan", DefaultExt = ".TapPlan" };
        if (dialog.ShowDialog(this) == true)
            SavePlan(dialog.FileName);
    }

    private void SavePlan(string path)
    {
        try
        {
            plan.Save(path);
            currentPath = path;
            isDirty = false;
            settings.RememberPlan(path);
            PathText.Text = path;
            UpdateTitle();
            UpdateStatus("Saved");
            AppendLog($"Saved plan: {path}");
        }
        catch (Exception ex)
        {
            ShowError("Could not save the test plan.", ex);
        }
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        AddStepTo(GetSelectedStep(), false);
    }

    private void RemoveSelectedStep_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedStep() is { } step)
            RemoveStep(step);
    }

    private void RebuildStepLibrary()
    {
        StepLibraryTree.Items.Clear();
        stepLibraryButtons.Clear();
        var filter = StepSearchBox?.Text?.Trim() ?? string.Empty;
        var types = availableStepTypes
            .Where(type => string.IsNullOrWhiteSpace(filter) ||
                           type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           GetStepDisplayName(type).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var group in types.GroupBy(GetStepCategory).OrderBy(group => group.Key))
        {
            var category = new TreeViewItem { Header = group.Key, IsExpanded = true, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var type in group.OrderBy(type => type.Name))
                category.Items.Add(CreateStepLibraryNode(type));
            StepLibraryTree.Items.Add(category);
        }
        UpdateStepLibraryButtons();
    }

    private TreeViewItem CreateStepLibraryNode(ITypeData type)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 2, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = GetStepDisplayName(type), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);
        var add = new Button { Content = "Add", Width = 44, Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(1, 1, 1, 1), ToolTip = "Add this step to the selected step's parent" };
        add.Click += (_, _) => AddStepInstance(type, GetSelectedStep(), false);
        Grid.SetColumn(add, 1);
        row.Children.Add(add);
        var addChild = new Button { Content = "Add Child", Width = 78, Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(1, 1, 1, 1), ToolTip = "Add this step as a child of the selected step" };
        addChild.Click += (_, _) => AddStepInstance(type, GetSelectedStep(), true);
        Grid.SetColumn(addChild, 2);
        row.Children.Add(addChild);
        stepLibraryButtons[type] = (add, addChild);
        return new TreeViewItem { Header = row, Tag = type };
    }

    private void UpdateStepLibraryButtons()
    {
        var selected = GetSelectedStep();
        foreach (var pair in stepLibraryButtons)
        {
            var canAdd = selected == null
                ? CanInsertType(plan.ChildTestSteps, pair.Key)
                : selected.Parent is ITestStepParent parent && CanInsertType(parent.ChildTestSteps, pair.Key);
            var canAddChild = selected != null && CanInsertType(selected.ChildTestSteps, pair.Key);
            pair.Value.Add.IsEnabled = CanEditPlan() && canAdd;
            pair.Value.AddChild.IsEnabled = CanEditPlan() && canAddChild;
        }
    }

    private static bool CanInsertType(TestStepList list, ITypeData stepType) =>
        !list.IsReadOnly && (list.Parent == null || TestStepList.AllowChild(TypeData.GetTypeData(list.Parent), stepType));

    private static IReadOnlyList<ITypeData> DiscoverAvailableStepTypes() => TypeData.GetDerivedTypes(TypeData.FromType(typeof(ITestStep)))
        .Where(type => type.CanCreateInstance)
        .Where(type => type is not TypeData concreteType || !IsTestAssembly(concreteType.Type.Assembly))
        .Where(type => !type.Name.Contains("UnitTests", StringComparison.OrdinalIgnoreCase))
        .ToList();

    private static IReadOnlyList<ITypeData> DiscoverBuiltInStepTypes()
    {
        var assemblies = new[]
        {
            typeof(DelayStep).Assembly,
            typeof(ScriptStep).Assembly,
            typeof(CanUdsRequestStep).Assembly,
            typeof(OperatorMeasurementStep).Assembly
        };
        return assemblies.Distinct().SelectMany(GetLoadableTypes)
            .Where(type => typeof(ITestStep).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
            .Where(type => !IsTestAssembly(type.Assembly))
            .Distinct()
            .Select(TypeData.FromType)
            .ToList();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static string GetStepCategory(ITypeData type)
    {
        if (type.Name.Contains("Uds", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Doip", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Can", StringComparison.OrdinalIgnoreCase))
            return "UDS Diagnostics";
        if (type.Name.Contains("Loop", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Sequence", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Parallel", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("If", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Repeat", StringComparison.OrdinalIgnoreCase))
            return "Flow Control";
        return "Basic Steps";
    }

    private static string GetStepDisplayName(ITypeData type) =>
        type.Attributes.OfType<DisplayAttribute>().FirstOrDefault()?.Name ?? type.Name.Split('.').Last();

    private void StepLibraryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: ITypeData type })
            UpdateStatus($"Selected step type: {GetStepDisplayName(type)}. Click Add selected step.");
    }

    private void StepSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildStepLibrary();

    private void AddSelectedLibraryStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepLibraryTree.SelectedItem is not TreeViewItem { Tag: ITypeData type })
        {
            ShowError("Select a step type from the left panel first.", null);
            return;
        }
        AddStepInstance(type, GetSelectedStep(), false);
    }

    private void AddStepTo(ITestStep? reference, bool asChild)
    {
        if (!CanEditPlan())
            return;

        try
        {
            var stepTypes = availableStepTypes;
            if (stepTypes.Count == 0)
            {
                ShowError("No parameterless TestStep plugin was found.", null);
                return;
            }

            var selector = new StepSelectionWindow(stepTypes) { Owner = this };
            if (selector.ShowDialog() != true || selector.SelectedType == null)
                return;

            var stepType = selector.SelectedType;
            AddStepInstance(stepType, reference, asChild);
        }
        catch (Exception ex)
        {
            ShowError("Could not add a test step.", ex);
        }
    }

    private void AddStepInstance(ITypeData stepType, ITestStep? reference, bool asChild)
    {
        try
        {
            var step = (ITestStep)stepType.CreateInstance(Array.Empty<object>());
            step.Name = GetUniqueStepName(GetStepDisplayName(stepType));

            if (reference == null)
            {
                if (!CanInsertType(plan.ChildTestSteps, stepType))
                {
                    ShowError($"The test plan cannot contain {stepType.Name}.", null);
                    return;
                }
                plan.ChildTestSteps.Add(step);
            }
            else if (asChild)
            {
                if (!CanInsertType(reference.ChildTestSteps, stepType))
                {
                    ShowError($"{reference.Name} cannot contain {stepType.Name}.", null);
                    return;
                }
                reference.ChildTestSteps.Add(step);
            }
            else
            {
                if (reference.Parent is not ITestStepParent parent)
                {
                    ShowError($"Could not find the parent of {reference.Name}.", null);
                    return;
                }
                var index = parent.ChildTestSteps.IndexOf(reference);
                if (index < 0 || !CanInsertType(parent.ChildTestSteps, stepType))
                {
                    ShowError($"The selected location cannot contain {stepType.Name}.", null);
                    return;
                }
                parent.ChildTestSteps.Add(step);
                parent.ChildTestSteps.Move(parent.ChildTestSteps.IndexOf(step), index + 1);
            }

            MarkDirty();
            RebuildTree();
            UpdateStatus($"Added {step.Name}");
            AppendLog($"Added step: {step.Name}");
        }
        catch (Exception ex)
        {
            ShowError("Could not add a test step.", ex);
        }
    }

    private string GetUniqueStepName(string baseName)
    {
        var names = EnumerateSteps(plan.ChildTestSteps).Select(step => step.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private ITestStep? GetSelectedStep() =>
        PlanTree.SelectedItem is TreeViewItem { Tag: ITestStep step } ? step : null;

    private static IEnumerable<ITestStep> EnumerateSteps(IEnumerable<ITestStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in EnumerateSteps(step.ChildTestSteps))
                yield return child;
        }
    }

    private static bool IsTestAssembly(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name ?? string.Empty;
        return assemblyName.Contains("UnitTests", StringComparison.OrdinalIgnoreCase) ||
               assemblyName.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (plan.IsRunning)
            return;

        var validationErrors = EnumerateSteps(plan.ChildTestSteps)
            .Select(step => (Step: step, Error: ((IDataErrorInfo)step).Error))
            .Where(item => !string.IsNullOrWhiteSpace(item.Error))
            .Select(item => $"{item.Step.Name}: {item.Error}")
            .ToList();
        if (validationErrors.Count > 0)
        {
            ShowError("The test plan has validation errors.", new InvalidOperationException(string.Join(Environment.NewLine, validationErrors)));
            return;
        }

        runCancellation = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        AbortButton.IsEnabled = true;
        UpdateStatus("Running");
        PlanRunStatusText.Text = "Running";
        AppendLog("Test plan started.");

        try
        {
            var result = await plan.ExecuteAsync(new[] { resultListener }, null, null, runCancellation.Token);
            UpdateStatus($"Finished: {result.Verdict}");
            PlanRunStatusText.Text = $"Completed: {result.Verdict}";
            AppendLog($"Test plan finished with verdict: {result.Verdict}");
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Aborted");
            PlanRunStatusText.Text = "Aborted";
            AppendLog("Test plan aborted.");
        }
        catch (Exception ex)
        {
            UpdateStatus("Error");
            PlanRunStatusText.Text = "Error";
            ShowError("Test plan execution failed.", ex);
        }
        finally
        {
            runCancellation.Dispose();
            runCancellation = null;
            RunButton.IsEnabled = true;
            AbortButton.IsEnabled = false;
            if (!plan.IsRunning)
                PlanRunStatusText.Text = PlanRunStatusText.Text == "Running" ? "Ready" : PlanRunStatusText.Text;
            RebuildTree();
        }
    }

    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        runCancellation?.Cancel();
        UpdateStatus("Aborting");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogText.Document.Blocks.Clear();
    }

    private void Duts_Click(object sender, RoutedEventArgs e) => ShowResources("DUTs", typeof(IDut), DutSettings.Current);

    private void Instruments_Click(object sender, RoutedEventArgs e) => ShowResources("Instruments", typeof(IInstrument), InstrumentSettings.Current);

    private void InstrumentDebug_Click(object sender, RoutedEventArgs e)
    {
        if (plan.IsRunning)
        {
            MessageBox.Show(this, "Stop the test plan before opening instrument debug.", "Instrument Debug",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new InstrumentDebugWindow { Owner = this }.ShowDialog();
    }

    private void Results_Click(object sender, RoutedEventArgs e) => ShowResources("Results", typeof(IResultListener), ResultSettings.Current);

    private void ShowResources(string title, Type resourceType, System.Collections.IList resources)
    {
        var window = new ResourceManagerWindow(title, resourceType, resources) { Owner = this };
        window.ShowDialog();
    }

    private void PropertiesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is PropertyRow property)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (property.Apply())
                {
                    MarkDirty();
                    RefreshTreeFor(property.Target);
                }
            });
        }
    }

    private bool ConfirmDiscard()
    {
        if (!isDirty)
            return true;
        return MessageBox.Show(this, "Discard the current plan and continue?", "OpenTAP Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (plan.IsRunning)
        {
            runCancellation?.Cancel();
            e.Cancel = true;
        }
        else if (isDirty && MessageBox.Show(this, "The current plan has unsaved changes. Exit anyway?", "OpenTAP Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        Log.RemoveListener(logListener);
        resultListener.StepCompleted -= ResultListener_StepCompleted;
        ComponentSettings.SaveAllCurrentSettings();
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.Save();
    }

    private void LogListener_MessageReceived(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AppendLog(message);
        });
    }

    private void ResultListener_StepCompleted(object? sender, TestStepRun stepRun)
    {
        var exception = stepRun.Exception == null ? string.Empty : $" Error: {stepRun.Exception.Message}";
        Dispatcher.BeginInvoke(() =>
        {
            var completedStep = EnumerateSteps(plan.ChildTestSteps)
                .FirstOrDefault(step => step.Id == stepRun.TestStepId);
            if (completedStep != null)
                completedStepRuns[completedStep] = stepRun;
            AppendLog($"Step {stepRun.TestStepName}: {stepRun.Verdict} [{stepRun.Duration:g}].{exception}");
            RebuildTree();
        });
    }

    private void AppendLog(string message)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = Brushes.Gray });
        paragraph.Inlines.Add(new Run(message) { Foreground = GetLogBrush(message) });
        LogText.Document.Blocks.Add(paragraph);
        LogText.ScrollToEnd();
    }

    private static Brush GetLogBrush(string message)
    {
        if (message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase))
            return Brushes.IndianRed;
        if (message.Contains("PASS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
            return Brushes.ForestGreen;
        if (message.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            return Brushes.DarkOrange;
        return Brushes.Black;
    }

    private void UpdateStatus(string status)
    {
        StatusText.Text = status;
    }

    private void MarkDirty()
    {
        isDirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = (isDirty ? "* " : "") + "OpenTAP WPF Editor";
    }

    private void RefreshTreeFor(object target)
    {
        RebuildTree();
        var node = FindTreeNode(PlanTree.Items, target);
        if (node != null)
            node.IsSelected = true;
    }

    private static TreeViewItem? FindTreeNode(ItemCollection items, object target)
    {
        foreach (TreeViewItem item in items)
        {
            if (ReferenceEquals(item.Tag, target))
                return item;
            var child = FindTreeNode(item.Items, target);
            if (child != null)
                return child;
        }
        return null;
    }

    private void ShowError(string message, Exception? exception)
    {
        var details = exception == null ? message : $"{message}\n\n{exception.Message}";
        MessageBox.Show(this, details, "OpenTAP Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        AppendLog(details);
    }
}

public sealed class PropertyRow : INotifyPropertyChanged
{
    private static readonly HashSet<string> MultilinePropertyNames = new(StringComparer.Ordinal)
    {
        "Source",
        "DecodeConfig",
        "Config1",
        "Message",
        "LogMessage",
        "LogHeader",
        "Query",
        "Arguments"
    };
    private readonly object target;
    private readonly AnnotationCollection annotations;
    private readonly AnnotationCollection memberAnnotation;
    private readonly IMemberData member;
    private string value;

    public string Name => memberAnnotation.Get<IDisplayAnnotation>()?.Name ?? member.Name;
    public string TypeName => target.GetType().GetProperty(member.Name)?.PropertyType.Name ?? member.Name;
    public bool IsBoolean => target.GetType().GetProperty(member.Name)?.PropertyType == typeof(bool);
    public bool IsMultiline => PropertyType == typeof(string) && MultilinePropertyNames.Contains(member.Name);
    private Type PropertyType => target.GetType().GetProperty(member.Name)?.PropertyType ?? typeof(object);
    public bool IsResourceReference => typeof(IResource).IsAssignableFrom(PropertyType);
    public bool IsReadOnly => !member.Writable;
    public object Target => target;
    public IReadOnlyList<IResource> ResourceOptions =>
        InstrumentSettings.Current.Cast<IResource>()
            .Where(resource => PropertyType.IsInstanceOfType(resource))
            .ToList();

    public IResource? SelectedResource
    {
        get => member.GetValue(target) as IResource;
        set
        {
            member.SetValue(target, value);
            Value = Format(value);
            OnPropertyChanged(nameof(SelectedResource));
            OnPropertyChanged(nameof(Value));
        }
    }

    public bool BoolValue
    {
        get => bool.TryParse(value, out var result) && result;
        set
        {
            Value = value ? bool.TrueString : bool.FalseString;
            Apply();
        }
    }

    public string Value
    {
        get => value;
        set
        {
            this.value = value;
            OnPropertyChanged(nameof(Value));
        }
    }

    public PropertyRow(object target, AnnotationCollection annotations, AnnotationCollection memberAnnotation, IMemberData member)
    {
        this.target = target;
        this.annotations = annotations;
        this.memberAnnotation = memberAnnotation;
        this.member = member;
        value = Format(memberAnnotation.Get<IObjectValueAnnotation>()?.Value);
    }

    public bool Apply()
    {
        try
        {
            if (IsResourceReference)
            {
                SelectedResource = ResourceOptions.FirstOrDefault(resource =>
                    string.Equals(resource.Name, value, StringComparison.OrdinalIgnoreCase));
                return true;
            }
            var type = target.GetType().GetProperty(member.Name)?.PropertyType ?? member.GetValue(target)?.GetType() ?? typeof(string);
            object? converted = type == typeof(bool)
                ? bool.Parse(value)
                : type.IsEnum
                ? Enum.Parse(type, value, true)
                : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            member.SetValue(target, converted);
            Value = Format(member.GetValue(target));
            return true;
        }
        catch
        {
            Value = Format(member.GetValue(target));
            return false;
        }
    }

    private static string Format(object? item) => item switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => item.ToString() ?? string.Empty
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class EditorSettings
{
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 820;
    public List<string> RecentPlans { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTAP", "WpfEditor", "settings.json");

    public static EditorSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(FilePath)) ?? new EditorSettings();
        }
        catch
        {
            // A damaged settings file should not prevent the editor from starting.
        }
        return new EditorSettings();
    }

    public void RememberPlan(string path)
    {
        RecentPlans.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        RecentPlans.Insert(0, path);
        if (RecentPlans.Count > 10)
            RecentPlans.RemoveRange(10, RecentPlans.Count - 10);
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are optional and must not block editor shutdown.
        }
    }
}

internal sealed class EditorDut : Dut
{
    public EditorDut()
    {
        Name = "DUT";
    }
}

internal sealed class ResourceManagerWindow : Window
{
    private readonly Type resourceType;
    private readonly System.Collections.IList resources;
    private readonly ListBox resourceList = new();
    private readonly ComboBox typeSelector = new();
    private readonly DataGrid propertyGrid = new();
    private readonly ObservableCollection<PropertyRow> properties = new();

    public ResourceManagerWindow(string title, Type resourceType, System.Collections.IList resources)
    {
        this.resourceType = resourceType;
        this.resources = resources;
        Title = title;
        Width = 820;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions[0].SharedSizeGroup = "ResourceList";
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        typeSelector.ItemsSource = GetResourceTypes().Select(type => type.Name).ToList();
        typeSelector.SelectedIndex = 0;
        Grid.SetColumn(typeSelector, 0);
        Grid.SetRow(typeSelector, 0);
        root.Children.Add(typeSelector);

        var add = new Button { Content = "Add", MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) };
        add.Click += Add_Click;
        Grid.SetColumn(add, 1);
        Grid.SetRow(add, 0);
        root.Children.Add(add);

        resourceList.SelectionChanged += ResourceList_SelectionChanged;
        Grid.SetColumn(resourceList, 0);
        Grid.SetRow(resourceList, 1);
        root.Children.Add(resourceList);

        propertyGrid.ItemsSource = properties;
        propertyGrid.AutoGenerateColumns = false;
        propertyGrid.CanUserAddRows = false;
        propertyGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        propertyGrid.Columns.Add(new DataGridTextColumn { Header = "Property", Binding = new System.Windows.Data.Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        propertyGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Value",
            CellTemplate = (DataTemplate)Application.Current.MainWindow.FindResource("PropertyValueTemplate"),
            CellEditingTemplate = (DataTemplate)Application.Current.MainWindow.FindResource("PropertyEditTemplate"),
            Width = new DataGridLength(1.5, DataGridLengthUnitType.Star)
        });
        propertyGrid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding("TypeName"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        propertyGrid.CellEditEnding += PropertyGrid_CellEditEnding;
        Grid.SetColumn(propertyGrid, 3);
        Grid.SetRow(propertyGrid, 1);
        root.Children.Add(propertyGrid);

        var remove = new Button { Content = "Remove", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        remove.Click += Remove_Click;
        Grid.SetColumn(remove, 0);
        Grid.SetRow(remove, 2);
        root.Children.Add(remove);

        var close = new Button { Content = "Close", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        Content = root;
        RefreshResources();
    }

    private List<Type> GetResourceTypes() => PluginManager.GetPlugins(resourceType).Cast<Type>()
        .Where(type => !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
        .Where(type => !(type.Assembly.GetName().Name ?? string.Empty).Contains("Test", StringComparison.OrdinalIgnoreCase))
        .Concat(resourceType == typeof(IDut) ? new[] { typeof(EditorDut) } : Enumerable.Empty<Type>())
        .OrderBy(type => type.Name)
        .ToList();

    private void RefreshResources()
    {
        resourceList.ItemsSource = null;
        resourceList.ItemsSource = resources.Cast<object>().OfType<IResource>().ToList();
        resourceList.DisplayMemberPath = "Name";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var types = GetResourceTypes();
        if (typeSelector.SelectedIndex < 0 || typeSelector.SelectedIndex >= types.Count)
        {
            MessageBox.Show(this, $"No creatable {resourceType.Name} plugin is available.", "No resource type", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var resource = (IResource)Activator.CreateInstance(types[typeSelector.SelectedIndex])!;
            resource.Name = GetUniqueName(resource.Name, resources.Cast<object>().OfType<IResource>().Select(item => item.Name));
            resources.Add(resource);
            ComponentSettings.SaveAllCurrentSettings();
            RefreshResources();
            resourceList.SelectedItem = resource;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not add resource", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (resourceList.SelectedItem is IResource resource)
        {
            resources.Remove(resource);
            ComponentSettings.SaveAllCurrentSettings();
            properties.Clear();
            RefreshResources();
        }
    }

    private void ResourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        properties.Clear();
        if (resourceList.SelectedItem is not object resource)
            return;
        var annotations = AnnotationCollection.Annotate(resource);
        foreach (var memberAnnotation in annotations.Get<IMembersAnnotation>()?.Members ?? Enumerable.Empty<AnnotationCollection>())
        {
            var member = memberAnnotation.Get<IMemberAnnotation>()?.Member;
            if (member?.Readable == true && member.Writable && memberAnnotation.Get<IObjectValueAnnotation>() != null)
                properties.Add(new PropertyRow(resource, annotations, memberAnnotation, member));
        }
    }

    private void PropertyGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is PropertyRow property)
            Dispatcher.BeginInvoke(() =>
            {
                property.Apply();
                ComponentSettings.SaveAllCurrentSettings();
            });
    }

    private static string GetUniqueName(string name, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name)) return name;
        for (var index = 2; ; index++)
        {
            var candidate = $"{name} {index}";
            if (!names.Contains(candidate)) return candidate;
        }
    }
}

internal sealed class WpfLogListener : TraceListener
{
    public event EventHandler<string>? MessageReceived;

    public override void TraceEvent(string source, LogEventType eventType, int id, string format)
    {
        MessageReceived?.Invoke(this, $"{eventType,-11} {source}: {format}");
    }
}

internal sealed class EditorResultListener : ResultListener
{
    public event EventHandler<TestStepRun>? StepCompleted;

    public override void OnTestStepRunCompleted(TestStepRun stepRun)
    {
        StepCompleted?.Invoke(this, stepRun);
    }
}

internal sealed class StepSelectionWindow : Window
{
    private readonly ComboBox stepSelector = new();
    private readonly IReadOnlyList<ITypeData> stepTypes;

    public ITypeData? SelectedType { get; private set; }

    public StepSelectionWindow(IReadOnlyList<ITypeData> stepTypes)
    {
        this.stepTypes = stepTypes;
        Title = "Add Test Step";
        Width = 520;
        Height = 170;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { Margin = new Thickness(14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.Children.Add(new TextBlock
        {
            Text = "Step type:",
            Margin = new Thickness(0, 0, 0, 6)
        });

        stepSelector.ItemsSource = stepTypes.Select(type => $"{type.Name}").ToList();
        stepSelector.SelectedIndex = 0;
        Grid.SetRow(stepSelector, 1);
        layout.Children.Add(stepSelector);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0) };
        var add = new Button { Content = "Add", IsDefault = true, MinWidth = 80 };
        add.Click += Add_Click;
        buttons.Children.Add(add);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        Content = layout;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (stepSelector.SelectedIndex >= 0)
        {
            SelectedType = stepTypes[stepSelector.SelectedIndex];
            DialogResult = true;
        }
    }
}

internal sealed class WpfUserInputInterface : IUserInputInterface
{
    public void RequestUserInput(object dataObject, TimeSpan timeout, bool modal)
    {
        var dispatcher = Application.Current.Dispatcher;
        var completed = dispatcher.Invoke(() => ShowRequest(dataObject, timeout));
        if (!completed)
            throw new TimeoutException("Request user input timed out.");
    }

    private static bool ShowRequest(object request, TimeSpan timeout)
    {
        var message = GetStringProperty(request, "Message") ?? request.ToString() ?? "OpenTAP requires user input.";
        var title = GetStringProperty(request, "Title") ?? GetStringProperty(request, "Name") ?? "OpenTAP";
        var buttons = GetStringProperty(request, "Buttons") ?? "OkCancel";
        var window = new Window
        {
            Title = title,
            Width = 460,
            MinWidth = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
        };

        var layout = new DockPanel { Margin = new Thickness(18) };
        var messageText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(messageText, Dock.Top);
        layout.Children.Add(messageText);
        var valueProperty = request.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        TextBox? valueInput = null;
        if (valueProperty?.CanWrite == true && valueProperty.PropertyType == typeof(double))
        {
            var inputPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            var unit = GetStringProperty(request, "Unit");
            if (!string.IsNullOrWhiteSpace(unit))
            {
                var unitText = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(unitText, Dock.Right);
                inputPanel.Children.Add(unitText);
            }
            valueInput = new TextBox
            {
                MinWidth = 220,
                Text = Convert.ToString(valueProperty.GetValue(request), CultureInfo.CurrentCulture) ?? ""
            };
            inputPanel.Children.Add(valueInput);
            DockPanel.SetDock(inputPanel, Dock.Top);
            layout.Children.Add(inputPanel);
        }
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var positive = new Button { Content = buttons.Equals("YesNo", StringComparison.OrdinalIgnoreCase) ? "Yes" : "OK", IsDefault = true, MinWidth = 80 };
        var negative = new Button { Content = buttons.Equals("YesNo", StringComparison.OrdinalIgnoreCase) ? "No" : "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var accepted = false;
        positive.Click += (_, _) =>
        {
            if (valueInput != null && valueProperty != null)
            {
                var parsed = double.TryParse(valueInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                    || double.TryParse(valueInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                if (!parsed)
                {
                    MessageBox.Show(window, "Enter a valid numeric value.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    valueInput.Focus();
                    valueInput.SelectAll();
                    return;
                }
                valueProperty.SetValue(request, value);
            }
            accepted = true;
            window.DialogResult = true;
        };
        negative.Click += (_, _) => { accepted = false; window.DialogResult = false; };
        buttonPanel.Children.Add(positive);
        buttonPanel.Children.Add(negative);
        layout.Children.Add(buttonPanel);
        window.Content = layout;

        var timer = timeout < TimeSpan.MaxValue
            ? new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = timeout }
            : null;
        if (timer != null)
        {
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window.Close();
            };
            timer.Start();
        }

        window.ShowDialog();
        timer?.Stop();
        if (window.IsVisible)
            window.Close();

        if (window.DialogResult == null)
            return false;

        SetEnumProperty(request, buttons.Equals("YesNo", StringComparison.OrdinalIgnoreCase) ? "Input1" : "Input2", accepted ? (buttons.Equals("YesNo", StringComparison.OrdinalIgnoreCase) ? "Yes" : "Ok") : (buttons.Equals("YesNo", StringComparison.OrdinalIgnoreCase) ? "No" : "Cancel"));
        SetEnumProperty(request, "Response", accepted ? "Submit" : "Cancel");
        return true;
    }

    private static string? GetStringProperty(object target, string name)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.GetValue(target)?.ToString();
    }

    private static void SetEnumProperty(object target, string name, string value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.CanWrite == true && property.PropertyType.IsEnum)
            property.SetValue(target, Enum.Parse(property.PropertyType, value, true));
    }
}