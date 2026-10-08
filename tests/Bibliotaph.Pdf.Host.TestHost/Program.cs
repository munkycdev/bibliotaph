using System.Globalization;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;

// Stands in for the app in IsolationTests.The_worker_dies_when_the_app_dies.
// Usage: Bibliotaph.Pdf.Host.TestHost <path to Bibliotaph.PdfWorker.exe>

await using var worker = new WorkerClient(new WorkerOptions { WorkerPath = args[0] });
var ping = await worker.SendAsync(new Request { Op = Op.Ping }, TimeSpan.FromSeconds(30));
if (!ping.Ok) return 1;

// Make the worker hang so it cannot notice the pipe closing and exit on its own;
// only the job object can end it when this process is killed.
_ = worker.SendAsync(new Request { Op = Op.Hang }, Timeout.InfiniteTimeSpan);
await Task.Delay(500);

Console.WriteLine(worker.WorkerProcessId!.Value.ToString(CultureInfo.InvariantCulture));
await Task.Delay(Timeout.Infinite);
return 0;
