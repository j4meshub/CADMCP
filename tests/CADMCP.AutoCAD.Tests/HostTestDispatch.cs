using System;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;

namespace CADMCP.AutoCAD.Tests;

// Test harness only. Native callbacks must consume exceptions locally; an outer
// try/await/catch alone cannot protect AutoCAD's unmanaged callback boundary.
internal static class HostTestDispatch
{
    internal static int ThreadId { get; private set; }
    internal static void BindHostThread() => ThreadId = Environment.CurrentManagedThreadId;
    internal static void EnsureCurrent(Document doc)
    {
        if (Environment.CurrentManagedThreadId != ThreadId)
            throw new InvalidOperationException("Host test attempted CAD access off the main thread");
        if (!ReferenceEquals(doc, Application.DocumentManager.MdiActiveDocument))
            throw new InvalidOperationException("Active test drawing changed; stopped without retry");
    }

    internal static async Task Command(Document doc, Action action)
    {
        Exception? failure = null;
        var pending = await ExecutionRegressionTests.InApplication(() =>
        {
            EnsureCurrent(doc);
            return Application.DocumentManager.ExecuteInCommandContextAsync(_ =>
            {
                try { EnsureCurrent(doc); action(); }
                catch (Exception error) { failure = error; }
                return Task.CompletedTask;
            }, null);
        });
        await pending;
        if (failure != null) throw new InvalidOperationException("Host test callback failed safely", failure);
    }

    internal static async Task Ready(Document doc)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? idle = null;
        idle = (_, __) =>
        {
            try
            {
                EnsureCurrent(doc);
                if (doc.Editor.IsQuiescent && string.IsNullOrWhiteSpace(doc.CommandInProgress)) completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        };
        await ExecutionRegressionTests.InApplication(() => { Application.Idle += idle; return true; });
        try
        {
            if (await Task.WhenAny(completion.Task, Task.Delay(20000)) != completion.Task)
                throw new TimeoutException("Test drawing did not become active and idle; no write started");
            await completion.Task;
        }
        finally { await ExecutionRegressionTests.InApplication(() => { Application.Idle -= idle; return true; }); }
    }

    internal static async Task Undo(Document doc)
    {
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CommandEventHandler end = (_, e) =>
        { try { if (e.GlobalCommandName.Equals("U", StringComparison.OrdinalIgnoreCase)) ended.TrySetResult(true); } catch (Exception error) { ended.TrySetException(error); } };
        CommandEventHandler fail = (_, e) =>
        { try { ended.TrySetException(new InvalidOperationException("Native U failed/cancelled: " + e.GlobalCommandName)); } catch (Exception error) { ended.TrySetException(error); } };
        try
        {
            await ExecutionRegressionTests.InApplication(() =>
            {
                EnsureCurrent(doc);
                if (!doc.Editor.IsQuiescent || !string.IsNullOrWhiteSpace(doc.CommandInProgress)) throw new InvalidOperationException("CAD busy before test U");
                doc.CommandEnded += end; doc.CommandFailed += fail; doc.CommandCancelled += fail;
                // No ExecuteInCommandContextAsync wrapper: test exactly ONE native U.
                doc.SendStringToExecute("_.U ", true, false, false);
                return true;
            });
            if (await Task.WhenAny(ended.Task, Task.Delay(15000)) != ended.Task)
                throw new TimeoutException("No native U completion event; result unknown; stopped without retry");
            await ended.Task;
        }
        finally
        {
            await ExecutionRegressionTests.InApplication(() =>
            { doc.CommandEnded -= end; doc.CommandFailed -= fail; doc.CommandCancelled -= fail; return true; });
        }
        await Ready(doc);
    }
}
