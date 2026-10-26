using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public sealed class AtomicFileStore : IReportContentReader
{
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly SemaphoreSlim _work = new(1, 1);
    public string Root { get; }
    public AtomicFileStore(string root, long maxBytes = 104857600)
    {
        if (string.IsNullOrWhiteSpace(root) || maxBytes <= 0) throw new AuditException("configuration_invalid");
        _maxBytes = maxBytes;
        Root = Path.GetFullPath(root);
        try
        {
            VerifyAncestors(Root);
            Directory.CreateDirectory(Root);
            ProtectDirectory(Root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AuditException("storage_unavailable"); }
    }
    public IDisposable AcquireLock()
    {
        try { return new FileStream(PathFor("instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AuditException("storage_unavailable"); }
    }
    // The market worker holds the instance lock and processes one assignment at a time.
    // Serialize capacity leases and keep conservative report/journal/temp headroom until delivery ends.
    // Recalculate on restart; no abandoned reservation file can block recovery.
    public async Task<IDisposable> ReserveWorkAsync(long bytes, int slots, CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken);
        try
        {
            await _writes.WaitAsync(cancellationToken);
            try
            {
                VerifyAncestors(Root);
                var files = Directory.EnumerateFiles(Root).Take(4097).ToArray();
                if (bytes <= 0 || slots <= 0 || files.Length + slots > 4096 ||
                    files.Sum(f => new FileInfo(f).Length) + bytes > _maxBytes)
                    throw new AuditException("storage_unavailable");
            }
            finally { _writes.Release(); }
            return new CapacityLease(_work);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _work.Release(); throw new AuditException("storage_unavailable"); }
        catch { _work.Release(); throw; }
    }
    private sealed class CapacityLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
    public async Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, bool overwrite, CancellationToken cancellationToken)
    {
        var path = PathFor(name);
        await _writes.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            temporary = PathFor(Guid.NewGuid().ToString("N") + ".tmp");
            if (!overwrite && File.Exists(path))
            {
                if ((await ReadAsync(name, cancellationToken)).AsSpan().SequenceEqual(bytes.Span)) return;
                throw new AuditException("immutable_output_conflict");
            }
            var files = Directory.EnumerateFiles(Root).Take(4097).ToArray();
            if (files.Length + (File.Exists(path) ? 1 : 2) > 4096 || files.Sum(f => new FileInfo(f).Length) + bytes.Length > _maxBytes)
                throw new AuditException("storage_unavailable");
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, streamOptions))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _ = PathFor(name);
            File.Move(temporary, path, overwrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AuditException("storage_unavailable"); }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _writes.Release();
        }
    }
    public async Task<byte[]> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathFor(name);
        if (new FileInfo(path).Length > 1048576) throw new AuditException("storage_unavailable");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }
    public IReadOnlyList<string> Names(string suffix)
    {
        VerifyAncestors(Root);
        var names = Directory.EnumerateFiles(Root).Take(4097).Select(Path.GetFileName).OfType<string>().ToArray();
        if (names.Length > 4096) throw new AuditException("storage_unavailable");
        return names.Where(n => n.EndsWith(suffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
    }
    public void Remove(string name) => File.Delete(PathFor(name));
    private string PathFor(string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            throw new AuditException("storage_unavailable");
        VerifyAncestors(Root);
        var path = Path.Combine(Root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new AuditException("storage_unavailable");
        return path;
    }
    private static void VerifyAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AuditException("storage_unavailable");
    }
    private static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new AuditException("storage_unavailable");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
