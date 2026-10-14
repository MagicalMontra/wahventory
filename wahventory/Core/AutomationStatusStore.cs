using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace wahventory.Core;

/// <summary>
/// Publishes per-process automation operation results without Dalamud dependencies.
/// A new store represents a new plugin session, even when the process ID is reused.
/// </summary>
public sealed class AutomationStatusStore
{
    public const string ProtocolHeader = "WAHVENTORY_STATUS_V1";
    public const string ProtocolFooter = "END_WAHVENTORY_STATUS";

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly Dictionary<string, AutomationOperationState> _states = new(StringComparer.Ordinal);

    public string SessionId { get; }
    public int ProcessId { get; }

    public AutomationStatusStore(string? directory = null, int? processId = null)
    {
        ProcessId = processId ?? Environment.ProcessId;
        if (ProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "A positive process ID is required.");

        _directory = Path.GetFullPath(directory ?? Path.GetTempPath());
        Directory.CreateDirectory(_directory);
        SessionId = Guid.NewGuid().ToString("D");

        foreach (var operation in new[] { "refresh", "discard" })
        {
            var initial = new AutomationOperationState(SessionId, operation, 0, "IDLE", 0, 0, string.Empty);
            Publish(initial);
            _states.Add(operation, initial);
        }
    }

    public string GetStatusPath(string operation)
    {
        ValidateOperation(operation);
        return Path.Combine(_directory, $"wahventory_status_v1_{ProcessId}_{operation}.txt");
    }

    public AutomationOperationState ReadState(string operation)
    {
        ValidateOperation(operation);
        lock (_sync)
            return _states[operation];
    }

    public bool IsRunning(string operation) => ReadState(operation).Status == "RUNNING";

    public long Begin(string operation, int total = 0)
    {
        ValidateOperation(operation);
        ValidateCounts(0, total);
        lock (_sync)
        {
            var previous = _states[operation];
            if (previous.Status == "RUNNING")
                throw new InvalidOperationException($"The {operation} operation is already running.");

            var next = previous with
            {
                RunId = checked(previous.RunId + 1),
                Status = "RUNNING",
                Completed = 0,
                Total = total,
                Message = string.Empty,
            };
            Commit(next);
            return next.RunId;
        }
    }

    public void Progress(string operation, int completed, int total)
    {
        ValidateOperation(operation);
        lock (_sync)
        {
            var current = _states[operation];
            if (current.Status != "RUNNING")
                return;

            ValidateCounts(completed, total);
            Commit(current with { Completed = completed, Total = total });
        }
    }

    public void Complete(string operation, string message = "", int? completed = null, int? total = null)
    {
        TransitionToTerminal(operation, "DONE", message, completed, total);
    }

    public void Fail(string operation, string message) => TransitionToTerminal(operation, "FAILED", message);

    public void Cancel(string operation, string message) => TransitionToTerminal(operation, "CANCELLED", message);

    private void TransitionToTerminal(string operation, string status, string message, int? completed = null, int? total = null)
    {
        ValidateOperation(operation);
        lock (_sync)
        {
            var current = _states[operation];
            if (current.Status != "RUNNING")
                return;

            var nextCompleted = completed ?? current.Completed;
            var nextTotal = total ?? current.Total;
            ValidateCounts(nextCompleted, nextTotal);
            if (status == "DONE" && nextCompleted != nextTotal)
                throw new InvalidOperationException("An operation cannot finish before all queued items are confirmed.");
            Commit(current with
            {
                Status = status,
                Completed = nextCompleted,
                Total = nextTotal,
                Message = message ?? string.Empty,
            });
        }
    }

    private void Commit(AutomationOperationState next)
    {
        // A publication failure leaves the in-memory state and run counter intact.
        Publish(next);
        _states[next.Operation] = next;
    }

    private void Publish(AutomationOperationState state)
    {
        var path = GetStatusPath(state.Operation);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        var text = string.Join("\n", new[]
        {
            ProtocolHeader,
            state.SessionId,
            state.Operation,
            state.RunId.ToString(CultureInfo.InvariantCulture),
            state.Status,
            state.Completed.ToString(CultureInfo.InvariantCulture),
            state.Total.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(Utf8.GetBytes(state.Message)),
            ProtocolFooter,
            string.Empty,
        });
        var bytes = Utf8.GetBytes(text);

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // The temporary file shares the destination directory and volume.
            ReplaceAtomically(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ReplaceAtomically(string temporary, string destination)
    {
        // Lua/stdio readers on Windows may briefly omit FILE_SHARE_DELETE.
        // Retry only the rename; never expose an in-place or partial record.
        const int attempts = 10;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt + 1 < attempts)
            {
                Thread.Sleep(2);
            }
            catch (UnauthorizedAccessException) when (attempt + 1 < attempts)
            {
                Thread.Sleep(2);
            }
        }
    }

    private static void ValidateOperation(string operation)
    {
        if (operation is not ("refresh" or "discard"))
            throw new ArgumentException("Only refresh and discard operations are supported.", nameof(operation));
    }

    private static void ValidateCounts(int completed, int total)
    {
        if (total < 0)
            throw new ArgumentOutOfRangeException(nameof(total), "Total cannot be negative.");
        if (completed < 0 || completed > total)
            throw new ArgumentOutOfRangeException(nameof(completed), "Completed must be between zero and total.");
    }
}

/// <summary>An immutable snapshot of the most recently published operation state.</summary>
public sealed record AutomationOperationState(
    string SessionId,
    string Operation,
    long RunId,
    string Status,
    int Completed,
    int Total,
    string Message);
