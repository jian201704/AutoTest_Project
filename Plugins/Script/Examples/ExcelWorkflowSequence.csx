using Tap.Plugins.Workflow;

var configuredWorkbooks = Environment.GetEnvironmentVariable("AIUM_WORKBOOKS");
if (string.IsNullOrWhiteSpace(configuredWorkbooks))
	throw new InvalidOperationException("Set AIUM_WORKBOOKS to an ordered semicolon-separated list of workbook paths.");

var workbookPaths = configuredWorkbooks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
	.Select(Path.GetFullPath)
	.ToArray();
if (workbookPaths.Length == 0 || workbookPaths.Any(path => !File.Exists(path)))
	throw new FileNotFoundException("AIUM_WORKBOOKS contains an empty or missing workbook path.");

var workflows = workbookPaths.Select(path => (Path: path, Workflow: ExcelWorkflowCompiler.Compile(path))).ToArray();
var finalVerdict = OpenTap.Verdict.Pass;
foreach (var entry in workflows)
{
	Log.Info("Starting workbook {0} ({1} enabled steps).", Path.GetFileName(entry.Path), entry.Workflow.EnabledStepCount);
	var run = await entry.Workflow.Plan.ExecuteAsync(CancellationToken);
	Log.Info("Workbook {0} completed with verdict {1}.", Path.GetFileName(entry.Path), run.Verdict);
	if (run.Verdict == OpenTap.Verdict.Pass) continue;

	finalVerdict = run.Verdict;
	Log.Error("Stopping the workbook sequence after {0} returned {1}.", Path.GetFileName(entry.Path), run.Verdict);
	break;
}

Verdict = finalVerdict;