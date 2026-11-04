using ManagerWindows.Core.Config;

namespace ManagerWindows.Core.Tests;

public class DataLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mw-loc-" + Guid.NewGuid().ToString("N"));
    private string Default => Path.Combine(_root, "default");
    private string Target => Path.Combine(_root, "target");

    public DataLocationTests() => Directory.CreateDirectory(Default);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Without_pointer_uses_default()
    {
        Assert.Equal((Default, (string?)null), DataLocation.Resolve(Default));
    }

    [Fact]
    public void Pointer_redirects_and_falls_back_when_target_missing()
    {
        Directory.CreateDirectory(Target);
        DataLocation.Set(Default, Target, cleanupDir: Default);
        Assert.Equal((Target, Default), DataLocation.Resolve(Default));

        DataLocation.ClearCleanup(Default);
        Assert.Equal((Target, (string?)null), DataLocation.Resolve(Default));

        // Ổ chứa thư mục mới bị rút ra: quay về mặc định thay vì tạo dữ liệu trống ở chỗ khác.
        Directory.Delete(Target);
        Assert.Equal(Default, DataLocation.Resolve(Default).Dir);

        // Chọn lại mặc định: file trỏ bị xoá.
        DataLocation.Set(Default, Default, null);
        Assert.False(File.Exists(Path.Combine(Default, DataLocation.PointerFile)));
    }

    [Fact]
    public void Validates_targets()
    {
        Assert.Equal("Data_Same", DataLocation.Validate(Default, Default));
        Assert.Equal("Data_Nested", DataLocation.Validate(Path.Combine(Default, "sub"), Default));
        Assert.Equal("Data_Nested", DataLocation.Validate(_root, Default));
        Assert.Equal("Data_Invalid", DataLocation.Validate("relative/path", Default));
        Assert.Null(DataLocation.Validate(Target, Default));
        Assert.True(Directory.Exists(Target));
    }

    [Fact]
    public void Copies_then_cleans_only_app_files()
    {
        File.WriteAllText(Path.Combine(Default, "config.json"), "{}");
        File.WriteAllText(Path.Combine(Default, "hibernated.json"), "[]");
        File.WriteAllText(Path.Combine(Default, "data.db"), "db");
        Directory.CreateDirectory(Path.Combine(Default, "logs"));
        File.WriteAllText(Path.Combine(Default, "logs", "app.log"), "x");
        File.WriteAllText(Path.Combine(Default, "user-notes.txt"), "keep me");
        DataLocation.Set(Default, Target, Default);

        DataLocation.CopyFiles(Default, Target);
        Assert.True(DataLocation.HasData(Target));
        Assert.True(File.Exists(Path.Combine(Target, "logs", "app.log")));

        DataLocation.Cleanup(Default);
        Assert.False(File.Exists(Path.Combine(Default, "config.json")));
        Assert.False(File.Exists(Path.Combine(Default, "data.db")));
        Assert.False(Directory.Exists(Path.Combine(Default, "logs")));
        Assert.True(File.Exists(Path.Combine(Default, "user-notes.txt")));
        Assert.True(File.Exists(Path.Combine(Default, DataLocation.PointerFile)));
    }
}
