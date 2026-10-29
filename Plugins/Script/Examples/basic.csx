Log.Info("C# script started");

var voltage = 15.0;
Log.Info($"voltage = {voltage}");
Assert.InRange(voltage, 14.25, 15.75, "voltage out of range");

for (var count = 0; count < 3; count++)
{
    CancellationToken.ThrowIfCancellationRequested();
    Log.Info($"iteration = {count + 1}");
}

Log.Info("C# script completed");