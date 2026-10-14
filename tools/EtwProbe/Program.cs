using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

// THROWAWAY: prints what Microsoft-Windows-Kernel-Network delivers, so the real tracker is written against facts.
var seconds = args.Length > 0 ? int.Parse(args[0]) : 20;
var kinds = new Dictionary<int, (string Task, string Opcode, string Payload, int Count)>();
var perProcess = new Dictionary<(int Pid, int Id), long>();

using var session = new TraceEventSession("NetRouteWidget-Probe") { StopOnDispose = true };
session.EnableProvider(new Guid("7dd42a49-5329-4832-8dfd-43d979153a88"), TraceEventLevel.Informational, 0x30);
session.Source.Dynamic.All += e =>
{
    lock (kinds)
    {
        var id = (int)e.ID;
        if (!kinds.TryGetValue(id, out var kind))
        {
            var fields = string.Join(", ", e.PayloadNames.Select(n => $"{n}={e.PayloadByName(n)?.GetType().Name}:{e.PayloadByName(n)}"));
            kind = (e.TaskName, e.OpcodeName, fields, 0);
        }
        kinds[id] = kind with { Count = kind.Count + 1 };
        if (e.PayloadNames.Contains("size") && e.PayloadNames.Contains("PID"))
        {
            var key = (Convert.ToInt32(e.PayloadByName("PID")), id);
            perProcess[key] = perProcess.GetValueOrDefault(key) + Convert.ToInt64(e.PayloadByName("size"));
        }
    }
};
var thread = new Thread(() => session.Source.Process()) { IsBackground = true };
thread.Start();
Console.WriteLine($"Tracing for {seconds} s. Use the network now (browse, download something).");
Thread.Sleep(TimeSpan.FromSeconds(seconds));
session.Dispose();
thread.Join(5000);

Console.WriteLine("--- event kinds (first sample of each) ---");
lock (kinds)
{
    foreach (var (id, kind) in kinds.OrderBy(x => x.Key))
        Console.WriteLine($"id {id,3} {kind.Task}/{kind.Opcode} x{kind.Count}: {kind.Payload}");
    Console.WriteLine("--- bytes by process and event id (top 15) ---");
    foreach (var ((pid, id), bytes) in perProcess.OrderByDescending(x => x.Value).Take(15))
    {
        string name;
        try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { name = "(gone)"; }
        Console.WriteLine($"{name,-24} pid {pid,6} id {id,3} {bytes,12:N0} bytes");
    }
}
