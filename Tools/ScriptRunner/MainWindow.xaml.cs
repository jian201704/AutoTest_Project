using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenTap;
using OpenTap.Plugins.BasicSteps;
using Tap.Plugins.Script;
using Tap.Plugins.Workflow;

namespace OpenTap.ScriptRunner;

public partial class MainWindow : Window
{
    private readonly RunnerLogListener logListener = new();
    private readonly RunnerResultListener resultListener = new();
    private readonly Stopwatch elapsed = new();
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? cancellation;
    private TestPlan? plan;
    private string? selectedFile;

    public MainWindow()
    {
        InitializeComponent();
        logListener.MessageReceived += LogListener_MessageReceived;
        resultListener.StepStarted += ResultListener_StepStarted;
        resultListener.StepCompleted += ResultListener_StepCompleted;
        Log.AddListener(logListener);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => ElapsedText.Text = elapsed.Elapsed.ToString(@"hh\:mm\:ss");
        Loaded += async (_, _) => await InitializeOpenTapAsync();
    }

    private async Task InitializeOpenTapAsync()
    {
        PluginManager.DirectoriesToSearch.Add(AppContext.BaseDirectory);
        PluginManager.DirectoriesToSearch.Add(Path.Combine(AppContext.BaseDirectory, "Packages", "OpenTAP"));
        PluginManager.DirectoriesToSearch.Add(Path.Combine(AppContext.BaseDirectory, "Packages"));
        PluginManager.DirectoriesToSearch.Add(Path.Combine(AppContext.BaseDirectory, "Packages", "Python"));
        try
        {
            await PluginManager.SearchAsync();
            SetStatus("Ready");
            AppendLog("OpenTAP plugins are ready.");
        }
        catch (Exception exception)
        {
            SetStatus("Plugin discovery failed");
            AppendLog(exception.ToString());
            RunButton.IsEnabled = false;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Runnable files (*.TapPlan;*.csx;*.xlsx;*.xlsm;*.py)|*.TapPlan;*.csx;*.xlsx;*.xlsm;*.py|OpenTAP Plan (*.TapPlan)|*.TapPlan|C# Script (*.csx)|*.csx|Excel Workflow (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|Python Script (*.py)|*.py",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            LoadFile(files[0]);
    }

    private void LoadFile(string path)
    {
        if (plan?.IsRunning == true)
            return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var extension = Path.GetExtension(fullPath);
            if (extension.Equals(".TapPlan", StringComparison.OrdinalIgnoreCase))
            {
                plan = TestPlan.Load(fullPath);
            }
            else if (extension.Equals(".csx", StringComparison.OrdinalIgnoreCase))
            {
                plan = new TestPlan();
                plan.Steps.Add(new ScriptStep
                {
                    Name = Path.GetFileNameWithoutExtension(fullPath),
                    ScriptFile = fullPath
                });
            }
            else if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            {
                var workflow = ExcelWorkflowCompiler.Compile(fullPath);
                plan = workflow.Plan;
                AppendLog($"Imported Excel workflow: {workflow.EnabledStepCount} enabled steps.");
            }
            else if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                var (pythonExecutable, argumentsPrefix) = ResolvePythonLauncher();
                plan = new TestPlan();
                plan.Steps.Add(new ProcessStep
                {
                    Name = Path.GetFileNameWithoutExtension(fullPath),
                    Application = pythonExecutable,
                    Arguments = $"{argumentsPrefix}\"{fullPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(fullPath)!,
                    WaitForEnd = true,
                    CheckExitCode = true,
                    AddToLog = true
                });
            }
            else
            {
                throw new NotSupportedException("Supported files are .TapPlan, .csx, .xlsx, .xlsm, and .py.");
            }

            selectedFile = fullPath;
            FilePathText.Text = fullPath;
            VerdictText.Text = "Verdict: Not run";
            SetStatus("Ready");
            AppendLog($"Loaded {Path.GetFileName(fullPath)}");
            RunButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            plan = null;
            selectedFile = null;
            FilePathText.Text = "No file selected";
            SetStatus("Load failed");
            AppendLog(exception.ToString());
            MessageBox.Show(this, exception.Message, "Could not load file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (plan == null || plan.IsRunning)
            return;

        var errors = EnumerateSteps(plan.ChildTestSteps)
            .Select(step => (Step: step, Error: ((IDataErrorInfo)step).Error))
            .Where(item => !string.IsNullOrWhiteSpace(item.Error))
            .Select(item => $"{item.Step.Name}: {item.Error}")
            .ToArray();
        if (errors.Length > 0)
        {
            SetStatus("Plan validation failed");
            AppendLog(string.Join(Environment.NewLine, errors));
            return;
        }

        cancellation = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        AbortButton.IsEnabled = true;
        VerdictText.Text = "Verdict: Running";
        SetStatus("Running");
        elapsed.Restart();
        timer.Start();
        AppendLog($"Started: {selectedFile}");
        try
        {
            var result = await plan.ExecuteAsync(new[] { resultListener }, null, null, cancellation.Token);
            SetVerdict(result.Verdict);
            SetStatus($"Completed: {result.Verdict}");
        }
        catch (OperationCanceledException)
        {
            SetVerdict(Verdict.Aborted);
            SetStatus("Aborted");
        }
        catch (Exception exception)
        {
            SetVerdict(Verdict.Error);
            SetStatus("Run failed");
            AppendLog(exception.ToString());
        }
        finally
        {
            timer.Stop();
            elapsed.Stop();
            cancellation.Dispose();
            cancellation = null;
            RunButton.IsEnabled = plan != null;
            AbortButton.IsEnabled = false;
        }
    }

    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        SetStatus("Aborting");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (plan?.IsRunning == true)
        {
            cancellation?.Cancel();
            e.Cancel = true;
            return;
        }
        Log.RemoveListener(logListener);
        resultListener.StepStarted -= ResultListener_StepStarted;
        resultListener.StepCompleted -= ResultListener_StepCompleted;
    }

    private void LogListener_MessageReceived(object? sender, string message) =>
        Dispatcher.BeginInvoke(() => AppendLog(message));

    private void ResultListener_StepStarted(object? sender, TestStepRun stepRun) =>
        Dispatcher.BeginInvoke(() => AppendLog($"Started step: {stepRun.TestStepName}"));

    private void ResultListener_StepCompleted(object? sender, TestStepRun stepRun) =>
        Dispatcher.BeginInvoke(() => AppendLog($"Step {stepRun.TestStepName}: {stepRun.Verdict} [{stepRun.Duration:g}]"));

    private void SetVerdict(Verdict verdict)
    {
        VerdictText.Text = $"Verdict: {verdict}";
        VerdictText.Foreground = verdict switch
        {
            Verdict.Pass => Brushes.ForestGreen,
            Verdict.Fail or Verdict.Error => Brushes.Firebrick,
            Verdict.Aborted => Brushes.DarkOrange,
            _ => Brushes.DimGray
        };
    }

    private void SetStatus(string status) => StatusText.Text = status;

    private void AppendLog(string message)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = Brushes.Gray });
        paragraph.Inlines.Add(new Run(message));
        LogText.Document.Blocks.Add(paragraph);
        while (LogText.Document.Blocks.Count > 1000)
            LogText.Document.Blocks.Remove(LogText.Document.Blocks.FirstBlock);
        LogText.ScrollToEnd();
    }

    private static IEnumerable<ITestStep> EnumerateSteps(IEnumerable<ITestStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in EnumerateSteps(step.ChildTestSteps))
                yield return child;
        }
    }

    private static (string Executable, string ArgumentsPrefix) ResolvePythonLauncher()
    {
        var configuredPython = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(configuredPython))
        {
            var configuredExecutable = FindExecutable(configuredPython.Trim().Trim('"'));
            if (configuredExecutable == null)
                throw new FileNotFoundException($"PYTHON points to an unavailable executable: '{configuredPython}'.");
            return (configuredExecutable, string.Empty);
        }

        var launcher = FindExecutable("py.exe");
        if (launcher != null)
            return (launcher, "-3 ");

        var python = FindExecutable("python.exe");
        if (python != null)
            return (python, string.Empty);

        throw new FileNotFoundException("Python was not found. Install Python or set the PYTHON environment variable to python.exe.");
    }

    private static string? FindExecutable(string executable)
    {
        if (File.Exists(executable))
            return Path.GetFullPath(executable);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

internal sealed class RunnerLogListener : TraceListener
{
    public event EventHandler<string>? MessageReceived;

    public override void TraceEvent(string source, LogEventType eventType, int id, string format) =>
        MessageReceived?.Invoke(this, $"{eventType,-11} {source}: {format}");
}

internal sealed class RunnerResultListener : ResultListener
{
    public event EventHandler<TestStepRun>? StepStarted;
    public event EventHandler<TestStepRun>? StepCompleted;

    public override void OnTestStepRunStart(TestStepRun stepRun) => StepStarted?.Invoke(this, stepRun);
    public override void OnTestStepRunCompleted(TestStepRun stepRun) => StepCompleted?.Invoke(this, stepRun);
}