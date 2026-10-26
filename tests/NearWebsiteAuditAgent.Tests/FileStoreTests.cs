using System.Text;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class FileStoreTests
{
    [Fact]
    public async Task Atomic_writes_round_trip_and_immutable_outputs_cannot_be_overwritten()
    {
        using var directory = new TemporaryDirectory();
        var store = new AtomicFileStore(directory.Path);
        await store.WriteAsync("result.json", "first"u8.ToArray(), false, default);
        Assert.Equal("first", Encoding.UTF8.GetString(await store.ReadAsync("result.json", default)));
        await Assert.ThrowsAsync<AuditException>(() => store.WriteAsync("result.json", "different"u8.ToArray(), false, default));
        Assert.Equal("first", Encoding.UTF8.GetString(await store.ReadAsync("result.json", default)));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }
    [Fact]
    public void A_second_instance_cannot_acquire_the_storage_root()
    {
        using var directory = new TemporaryDirectory();
        using var first = new AtomicFileStore(directory.Path).AcquireLock();
        Assert.Throws<AuditException>(() => new AtomicFileStore(directory.Path).AcquireLock());
    }
    [Theory]
    [InlineData("../outside.json")]
    [InlineData("sub/result.json")]
    [InlineData("C:\\outside.json")]
    public async Task External_names_cannot_escape_the_storage_root(string filename)
    {
        using var directory = new TemporaryDirectory();
        await Assert.ThrowsAsync<AuditException>(() =>
            new AtomicFileStore(directory.Path).WriteAsync(filename, "secret"u8.ToArray(), false, default));
    }
    [Fact]
    public async Task Capacity_exhaustion_does_not_corrupt_an_existing_record()
    {
        using var directory = new TemporaryDirectory();
        var store = new AtomicFileStore(directory.Path, 10);
        await store.WriteAsync("record.json", "first"u8.ToArray(), true, default);
        await Assert.ThrowsAsync<AuditException>(() => store.WriteAsync("record.json", new byte[20], true, default));
        Assert.Equal("first", Encoding.UTF8.GetString(await store.ReadAsync("record.json", default)));
    }
}
