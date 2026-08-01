using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BizHawkNetplay.Core.Diag;

/// <summary>
/// A plain text log on disk, one file per launch that produces one, keeping only the most recent few.
///
/// It exists because the in-window log is the wrong shape for the case that matters: diagnosing a
/// session usually needs the OTHER player's log, and asking someone to select a scrolling textbox
/// and paste it before they close the emulator loses more reports than it collects. A file they can
/// attach afterwards does not.
///
/// <b>Nothing is created until <see cref="Activate"/> is called.</b> Opening a tool window and
/// closing it again is not a session, and if every launch wrote a file, a handful of idle open/close
/// cycles would rotate away every log anyone actually wanted — quietly, and precisely when someone
/// had been asked to go and find one. Lines written before activation are held in memory and land in
/// the file if one is ever made, so the run-up (which core loaded, what the probe said) is not lost
/// to the deferral. Pruning happens at activation for the same reason: an idle launch must not
/// delete anything.
///
/// Lives in Core rather than beside its caller because there is nothing emulator-specific about it,
/// and because the caller cannot be tested — the tool assembly needs a live EmuHawk. The directory
/// is a parameter for the same reason: where logs belong is the application's decision, and passing
/// it in is what lets a test point this at a temporary folder.
///
/// Nothing here may throw at the caller. A full disk, a read-only roaming profile, or antivirus
/// holding the handle all end with logging quietly off — logging failing is an inconvenience, while
/// logging taking down a session would be this class causing the very problem it exists to explain.
/// </summary>
public sealed class RotatingLogFile : IDisposable
{
    /// <summary>
    /// Lines held before activation. Generous — the run-up to a connection is a few dozen lines even
    /// with a capability probe in it — but finite, because a window left open all day with verbose
    /// logging on and no session must not grow a buffer for ever.
    /// </summary>
    private const int MaxBufferedLines = 1000;

    /// <summary>
    /// Ceiling on one file, after which writing stops and says so.
    ///
    /// Rotation bounds how many files accumulate across launches; nothing bounded a single one. That
    /// matters because not every line here is ours: a refused handshake logs the reason the far end
    /// gave, so a peer that keeps reconnecting with a large reason string is choosing both the
    /// content and the volume of what this writes to disk. Generous against any real session — a
    /// long verbose one is a few megabytes — and small enough that filling a disk is not on the
    /// table. Stopping beats rotating: the start of a session is the part worth keeping, and a
    /// flood would otherwise rotate it away.
    /// </summary>
    public const long DefaultMaxFileBytes = 32L * 1024 * 1024;

    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _keepFiles;
    private readonly string? _header;
    private readonly long _maxFileBytes;
    private long _written;
    private bool _capped;

    private Queue<string>? _pending = new();
    private int _droppedBeforeActivation;
    private StreamWriter? _writer;
    private bool _disposed;

    private RotatingLogFile(string directory, string prefix, int keepFiles, string? header, long maxFileBytes)
    {
        _directory = directory;
        _prefix = prefix;
        _keepFiles = keepFiles;
        _header = header;
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>Full path of the file being written, or null while nothing has been created.</summary>
    public string? Path { get; private set; }

    /// <summary>True once a file exists and writes are reaching it.</summary>
    public bool IsOpen => _writer != null;

    /// <summary>
    /// Prepare a log without creating anything. Call <see cref="Activate"/> when something worth
    /// keeping happens.
    /// </summary>
    /// <param name="prefix">Filename stem; the timestamp and ".log" are appended.</param>
    /// <param name="keepFiles">Total logs to keep in the folder, including the one this makes.</param>
    /// <param name="header">Written first if a file is ever created, and flushed immediately.</param>
    /// <param name="maxFileBytes">Ceiling for this one file; see <see cref="DefaultMaxFileBytes"/>.</param>
    public static RotatingLogFile Deferred(
        string directory, string prefix, int keepFiles, string? header = null,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        if (directory == null) throw new ArgumentNullException(nameof(directory));
        if (string.IsNullOrEmpty(prefix)) throw new ArgumentException("A prefix is required", nameof(prefix));
        if (keepFiles < 1) throw new ArgumentOutOfRangeException(nameof(keepFiles));
        if (maxFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        return new RotatingLogFile(directory, prefix, keepFiles, header, maxFileBytes);
    }

    /// <summary>
    /// Create the file, rotate old ones out, and write the header plus everything buffered so far.
    /// Idempotent, and safe to call from anywhere: a failure leaves the log in its buffering state,
    /// where every operation is still harmless.
    /// </summary>
    /// <returns>True if a file is now being written.</returns>
    public bool Activate()
    {
        if (_disposed) return false;
        if (_writer != null) return true;
        try
        {
            Directory.CreateDirectory(_directory);
            Prune(_directory, _prefix, _keepFiles - 1);
            var path = System.IO.Path.Combine(
                _directory, $"{_prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            // FileShare.ReadWrite so the file can be opened, copied or tailed while we hold it —
            // someone being asked for their log should not have to close the emulator first.
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
            Path = path;

            if (_header != null) _writer.WriteLine(_header);
            if (_droppedBeforeActivation > 0)
                _writer.WriteLine(
                    $"[{_droppedBeforeActivation} earlier line(s) dropped before this file was created]");
            if (_pending != null)
                foreach (var line in _pending) _writer.WriteLine(line);
            _pending = null;
            _writer.Flush();
            return true;
        }
        catch
        {
            Close();
            _pending = null; // the buffer had one destination and it failed; do not hoard for ever
            return false;
        }
    }

    /// <summary>
    /// Append one already-formatted line — to the file if there is one, otherwise to the buffer that
    /// will seed it. Buffered rather than flushed: the caller is a UI thread that also owns a frame
    /// clock, so this must not wait on a disk.
    /// </summary>
    public void Write(string line)
    {
        if (_disposed) return;
        if (_writer == null)
        {
            if (_pending == null) return; // activation failed; stay quiet
            if (_pending.Count >= MaxBufferedLines) { _pending.Dequeue(); _droppedBeforeActivation++; }
            _pending.Enqueue(line);
            return;
        }
        if (_capped) return;
        try
        {
            // Counted in characters rather than encoded bytes: the difference is a rounding error
            // against a 32 MiB ceiling, and measuring it exactly would mean encoding every line
            // twice on a path the UI thread runs.
            _written += line.Length + 2;
            if (_written >= _maxFileBytes)
            {
                _capped = true;
                _writer.WriteLine(
                    $"[log reached its {_maxFileBytes} byte ceiling — nothing further will be " +
                    "written to this file]");
                _writer.Flush();
                return;
            }
            _writer.WriteLine(line);
        }
        catch { Close(); } // a writer that has started failing will not recover; stop trying
    }

    /// <summary>
    /// Push what is buffered to disk. A no-op until <see cref="Activate"/>.
    ///
    /// Meant for the events worth being durable — session boundaries, connection changes — and a
    /// slow timer, rather than every line. That puts the cost where it buys something: if the
    /// process is killed or hangs, the log still ends with what was happening, and the most a crash
    /// can cost is a second of routine chatter.
    /// </summary>
    public void Flush()
    {
        try { _writer?.Flush(); }
        catch { Close(); }
    }

    /// <summary>Closes the file if one was made. A log that never activated leaves no trace.</summary>
    public void Dispose()
    {
        _disposed = true;
        try { _writer?.Flush(); } catch { }
        Close();
        _pending = null;
    }

    private void Close()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>Delete all but the newest <paramref name="keep"/> matching logs, so this cannot grow
    /// without bound in a folder nobody thinks to look at.</summary>
    private static void Prune(string directory, string prefix, int keep)
    {
        try
        {
            var files = new List<FileInfo>(new DirectoryInfo(directory).GetFiles(prefix + "-*.log"));
            if (files.Count <= keep) return;
            // Newest first by name, not by timestamp: the name carries the launch time, and it sorts
            // correctly because it is written big-endian. File times can be rewritten by a copy or a
            // restore, which would then delete the wrong logs.
            files.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
            for (int i = keep; i < files.Count; i++)
                try { files[i].Delete(); } catch { /* in use, or gone already */ }
        }
        catch { /* the folder is new, unreadable, or someone is holding it — not worth reporting */ }
    }

    /// <summary>The timestamp prefix a logged line carries.</summary>
    public static string Stamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
