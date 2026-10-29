using Tap.Plugins.Workflow;

var workbookPath = Environment.GetEnvironmentVariable("AIUM_WORKBOOK_PATH");
if (string.IsNullOrWhiteSpace(workbookPath))
	throw new InvalidOperationException("Set AIUM_WORKBOOK_PATH to the AIUM_Eol.xlsx file before running this CLI script.");
workbookPath = Path.GetFullPath(workbookPath);
var workflow = ExcelWorkflowCompiler.Compile(workbookPath);
Log.Info("AIUM workflow compiled: {0} enabled steps.", workflow.EnabledStepCount);

var run = await workflow.Plan.ExecuteAsync(CancellationToken);
Log.Info("AIUM workflow completed with verdict: {0}.", run.Verdict);
Verdict = run.Verdict;