namespace ManagerWindows.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Cách xoá một file: xoá hẳn hoặc đưa vào Thùng rác (Windows).</summary>
public interface IFileDeleter
{
    void Delete(string path);
    void Recycle(string path);
}

public sealed class PermanentDeleter : IFileDeleter
{
    public void Delete(string path) => File.Delete(path);

    // Trên nền tảng không có Thùng rác thì xoá hẳn; bản Windows thay bằng RecycleBinDeleter.
    public void Recycle(string path) => File.Delete(path);
}

/// <summary>Tạo/gỡ liên kết thư mục. Windows dùng junction, test trên Linux dùng symlink.</summary>
public interface ILinkApi
{
    void CreateDirectoryLink(string linkPath, string targetPath);
    /// <summary>Trả về đích của liên kết, null nếu không phải liên kết.</summary>
    string? GetLinkTarget(string path);
    void RemoveLink(string linkPath);
}

public sealed record VolumeInfo(bool IsNtfs, bool IsFixed, long FreeBytes, string Root);

public interface IVolumeApi
{
    VolumeInfo? Get(string path);
}

public interface IDirectoryCopier
{
    void Copy(string source, string destination, CancellationToken ct);
}
