namespace Bibliotaph.Processing.Tests;

public sealed class SecretFileTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-secrets-").FullName;

    string FilePath => Path.Combine(_dir, "data", "secrets.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Secrets_are_kept_between_starts_and_forgotten_by_name_or_prefix()
    {
        var file = new SecretFile(FilePath);
        Assert.False(file.Exists);
        Assert.Null(file.Find("web:device"));

        Assert.True(file.Remember("web:device", "key-1"));
        Assert.True(file.Remember("pdf:bbb", "two"));
        Assert.True(file.Remember("pdf:aaa", "one"));
        Assert.True(file.Remember("web:device", "key-2"));

        var again = new SecretFile(FilePath);
        Assert.Equal("key-2", again.Find("web:device"));
        Assert.Equal(["pdf:aaa", "pdf:bbb"], again.Names("pdf:"));

        again.Forget("pdf:aaa");
        Assert.Null(new SecretFile(FilePath).Find("pdf:aaa"));
        Assert.Equal(1, again.ForgetAll("pdf:"));
        Assert.Equal(1, again.ForgetAll(""));
        Assert.Empty(new SecretFile(FilePath).Names(""));
        Assert.Equal(["secrets.json"], Directory.GetFiles(Path.GetDirectoryName(FilePath)!).Select(Path.GetFileName));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Only_the_owner_can_read_it()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix file modes.");
        var file = new SecretFile(FilePath);
        file.Remember("ai:http://localhost:1234", "secret");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));

        File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Equal("secret", new SecretFile(FilePath).Find("ai:http://localhost:1234"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void Create_writes_the_first_secrets_once_even_with_none()
    {
        var file = new SecretFile(FilePath);
        Assert.True(file.Create(new Dictionary<string, string> { ["storage:abc"] = "refresh" }));
        Assert.True(file.Exists);
        Assert.False(file.Create(new Dictionary<string, string> { ["storage:abc"] = "other" }));
        Assert.Equal("refresh", new SecretFile(FilePath).Find("storage:abc"));

        var empty = new SecretFile(Path.Combine(_dir, "empty", "secrets.json"));
        Assert.True(empty.Create(new Dictionary<string, string>()));
        Assert.True(empty.Exists);
    }

    [Fact]
    public void A_damaged_file_reads_as_empty_and_the_next_save_replaces_it()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var written = new List<string>();
        var file = new SecretFile(FilePath, written: written.Add);

        Assert.Null(file.Find("web:device"));
        Assert.True(file.Remember("web:device", "key"));
        Assert.Equal([FilePath], written);
        Assert.Equal("key", new SecretFile(FilePath).Find("web:device"));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_save_that_fails_keeps_what_was_there()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix folder modes.");
        Assert.SkipWhen(Environment.UserName == "root", "Root writes to a read-only folder.");
        var file = new SecretFile(FilePath);
        file.Remember("web:device", "key");
        var folder = Path.GetDirectoryName(FilePath)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(file.Remember("web:device", "other"));
            Assert.Equal("key", file.Find("web:device"));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
