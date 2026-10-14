using System.Text;
using wahventory.Core;

var checks = 0;
void Check(bool value, string description)
{
    checks++;
    if (!value) throw new Exception(description);
}
void Throws<T>(Action work, string description) where T : Exception
{
    checks++;
    try { work(); }
    catch (T) { return; }
    throw new Exception(description);
}
void ThrowsPublishFailure(Action work, string description)
{
    checks++;
    try { work(); }
    catch (IOException) { return; }
    catch (UnauthorizedAccessException) { return; }
    throw new Exception(description);
}
string[] ReadProtocol(string path)
{
    var bytes = File.ReadAllBytes(path);
    Check(!bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "Protocol has no UTF-8 BOM");
    var text = new UTF8Encoding(false, true).GetString(bytes);
    Check(text.EndsWith('\n'), "Protocol ends with newline");
    var lines = text.Split('\n');
    Check(lines.Length == 10, "Exactly nine protocol lines and trailing newline");
    Check(lines[0] == AutomationStatusStore.ProtocolHeader, "Header intact");
    Check(lines[8] == AutomationStatusStore.ProtocolFooter, "Footer intact");
    Check(Guid.TryParse(lines[1], out _), "Session is a GUID");
    _ = long.Parse(lines[3]); _ = int.Parse(lines[5]); _ = int.Parse(lines[6]);
    _ = Encoding.UTF8.GetString(Convert.FromHexString(lines[7]));
    return lines;
}

var testParent = Path.GetFullPath(AppContext.BaseDirectory);
var root = Path.GetFullPath(Path.Combine(testParent, "wahventory-store-tests-" + Guid.NewGuid().ToString("N")));
Check(root.StartsWith(testParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Test directory stays inside its workspace output directory");
Directory.CreateDirectory(root);
try
{
    var store = new AutomationStatusStore(root, 111);
    foreach (var operation in new[] { "refresh", "discard" })
    {
        var lines = ReadProtocol(store.GetStatusPath(operation));
        Check(lines[1] == store.SessionId && lines[2] == operation && lines[3] == "0" && lines[4] == "IDLE", "Fresh operation starts IDLE/run0");
        Check(!store.IsRunning(operation), "Fresh operation is not running");
        store.Complete(operation); store.Fail(operation, "ignored"); store.Cancel(operation, "ignored");
        Check(store.ReadState(operation).Status == "IDLE", "Terminal methods do not alter IDLE");
    }
    Check(store.Begin("discard", 3) == 1, "First run ID is one");
    Check(store.IsRunning("discard"), "Begin publishes running");
    Throws<InvalidOperationException>(() => store.Begin("discard", 5), "Duplicate Begin rejected");
    Check(store.ReadState("discard").Total == 3 && store.ReadState("discard").RunId == 1, "Duplicate Begin does not mutate state");
    store.Progress("discard", 2, 3);
    Check(store.ReadState("discard").Completed == 2, "Progress publishes item count");
    Throws<InvalidOperationException>(() => store.Complete("discard"), "Partial progress cannot publish DONE");
    Check(store.IsRunning("discard"), "Rejected partial completion remains RUNNING");
    var unicode = "Done\n\r\0 สวัสดี 🐟";
    store.Complete("discard", unicode, completed: 3);
    var done = store.ReadState("discard");
    var protocol = ReadProtocol(store.GetStatusPath("discard"));
    Check(protocol[4] == "DONE" && protocol[5] == "3" && protocol[6] == "3", "DONE publishes explicit counts");
    Check(Encoding.UTF8.GetString(Convert.FromHexString(protocol[7])) == unicode, "UTF-8 hex protects controls and Unicode");
    var doneBytes = File.ReadAllBytes(store.GetStatusPath("discard"));
    store.Cancel("discard", "late cancel"); store.Fail("discard", "late error"); store.Progress("discard", 0, 0); store.Complete("discard", "late done");
    Check(store.ReadState("discard") == done, "First terminal state is preserved");
    Check(doneBytes.SequenceEqual(File.ReadAllBytes(store.GetStatusPath("discard"))), "Stale callbacks do not rewrite terminal file");
    Check(store.Begin("discard", 2) == 2, "Next run increments ID");
    store.Fail("discard", "failure");
    Check(store.ReadState("discard").Status == "FAILED", "Failure recorded");
    store.Cancel("discard", "late cancellation");
    Check(store.ReadState("discard").Message == "failure", "Cancellation cannot erase failure");
    Check(store.Begin("discard") == 3, "Third run increments ID");
    store.Cancel("discard", "cancelled");
    Check(store.ReadState("discard").Status == "CANCELLED", "Cancellation recorded");
    Check(store.Begin("refresh") == 1, "Refresh has independent run counter");
    store.Complete("refresh", "ready");
    Check(store.ReadState("refresh").Status == "DONE", "Zero-item refresh completes");

    var isolated = new AutomationStatusStore(root, 222);
    var isolatedBytes = File.ReadAllBytes(isolated.GetStatusPath("discard"));
    store.Begin("discard", 1); store.Complete("discard", "one", 1, 1);
    Check(isolatedBytes.SequenceEqual(File.ReadAllBytes(isolated.GetStatusPath("discard"))), "Process IDs isolate files");
    var oldSession = store.SessionId;
    var restarted = new AutomationStatusStore(root, 111);
    Check(restarted.SessionId != oldSession, "Plugin session changes even when PID reused");
    Check(restarted.ReadState("discard").RunId == 0 && ReadProtocol(restarted.GetStatusPath("discard"))[4] == "IDLE", "Restart replaces stale terminal result");

    // Force atomic destination replacement to fail after its temporary file is written.
    var failureStore = new AutomationStatusStore(root, 333);
    var failurePath = failureStore.GetStatusPath("discard");
    File.Delete(failurePath); Directory.CreateDirectory(failurePath);
    ThrowsPublishFailure(() => failureStore.Begin("discard", 1), "Publish failure surfaced");
    Check(failureStore.ReadState("discard").Status == "IDLE" && failureStore.ReadState("discard").RunId == 0, "Failed publication commits neither state nor counter");
    Directory.Delete(failurePath);
    Check(failureStore.Begin("discard", 1) == 1, "Retry uses the uncommitted run ID");
    var beforeFailure = failureStore.ReadState("discard");
    File.Delete(failurePath); Directory.CreateDirectory(failurePath);
    ThrowsPublishFailure(() => failureStore.Complete("discard", "not published", 1, 1), "Terminal publish failure surfaced");
    Check(failureStore.ReadState("discard") == beforeFailure && failureStore.IsRunning("discard"), "Failed DONE does not pretend to be published");
    Directory.Delete(failurePath);
    failureStore.Fail("discard", "retry reported error");
    Check(ReadProtocol(failurePath)[4] == "FAILED", "Failure can be retried after publication resumes");

    Throws<ArgumentException>(() => store.Begin("../escape"), "Operation names cannot escape destination");
    Throws<ArgumentOutOfRangeException>(() => store.Begin("refresh", -1), "Negative total rejected");
    Throws<ArgumentOutOfRangeException>(() => new AutomationStatusStore(root, 0), "Invalid PID rejected");
    store.Begin("refresh", 2);
    Throws<ArgumentOutOfRangeException>(() => store.Progress("refresh", 3, 2), "Impossible progress rejected");
    Check(store.ReadState("refresh").Completed == 0, "Rejected progress leaves state unchanged");
    store.Complete("refresh", "fine", 2, 2);

    var heldReaderStore = new AutomationStatusStore(root, 445);
    heldReaderStore.Begin("discard", 1);
    Task blockedWriter;
    using (var readerStarted = new ManualResetEventSlim())
    {
        using (var heldReader = new FileStream(heldReaderStore.GetStatusPath("discard"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            blockedWriter = Task.Run(() =>
            {
                readerStarted.Set();
                heldReaderStore.Progress("discard", 1, 1);
            });
            readerStarted.Wait();
            Thread.Sleep(4);
            Check(!blockedWriter.IsCompleted, "A non-delete-sharing reader prevents publishing until it releases the file");
        }
        blockedWriter.GetAwaiter().GetResult();
    }
    Check(heldReaderStore.ReadState("discard").Completed == 1, "Bounded atomic-rename retry succeeds after a short reader closes");
    heldReaderStore.Complete("discard");

    // Readers share delete access as required for atomic replacement on Windows.
    var atomic = new AutomationStatusStore(root, 444);
    atomic.Begin("discard", 100);
    var readerErrors = new List<Exception>();
    var reader = Task.Run(() =>
    {
        try
        {
            for (var i = 0; i < 1000; i++)
            {
                using var stream = new FileStream(atomic.GetStatusPath("discard"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var text = new StreamReader(stream, Encoding.UTF8);
                var lines = text.ReadToEnd().Split('\n');
                if (lines.Length != 10 || lines[0] != AutomationStatusStore.ProtocolHeader || lines[8] != AutomationStatusStore.ProtocolFooter)
                    throw new Exception("Reader saw a partial publication");
            }
        }
        catch (Exception error) { readerErrors.Add(error); }
    });
    for (var i = 1; i <= 100; i++) atomic.Progress("discard", i, 100);
    atomic.Complete("discard", "atomic reader passed");
    reader.GetAwaiter().GetResult();
    Check(readerErrors.Count == 0, "Concurrent reader sees only complete records");
    Check(!Directory.GetFiles(root, "*.tmp.*").Any(), "Successful and failed publishes clean temporary files");
    Console.WriteLine($"PASS: {checks} checks, 100 atomic updates, 1000 concurrent complete-record reads.");
}
finally
{
    if (!root.StartsWith(testParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Refusing to remove a test directory outside the workspace output directory.");
    Directory.Delete(root, recursive: true);
}
