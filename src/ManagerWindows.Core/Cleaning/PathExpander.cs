using System.IO.Enumeration;
using System.Text.RegularExpressions;
using ManagerWindows.Core.Abstractions;

namespace ManagerWindows.Core.Cleaning;

/// <summary>
/// Mở rộng đường dẫn trong luật: biến môi trường (%LOCALAPPDATA%) và ký tự đại diện trong từng đoạn,
/// ví dụ <c>%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache</c> → cache của mọi profile Chrome.
/// </summary>
public static partial class PathExpander
{
    [GeneratedRegex("%[^%\\\\/]+%")]
    private static partial Regex UnresolvedVar();

    public static IEnumerable<string> Expand(string pattern)
    {
        var expanded = Environment.ExpandEnvironmentVariables(pattern);
        // Biến môi trường không tồn tại (vd. chạy dưới SYSTEM không có %APPDATA% của user) thì bỏ qua,
        // tránh hiểu nhầm thành thư mục tên "%APPDATA%".
        if (UnresolvedVar().IsMatch(expanded)) return [];
        expanded = expanded.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return ExpandParts(expanded.Split(Path.DirectorySeparatorChar))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExpandParts(string[] parts)
    {
        var idx = Array.FindIndex(parts, p => p.IndexOfAny(['*', '?']) >= 0);
        if (idx < 0)
        {
            var full = JoinBase(parts);
            if (Directory.Exists(full)) yield return Path.GetFullPath(full);
            yield break;
        }
        if (idx == 0) yield break; // không cho đại diện ngay ở gốc

        var baseDir = JoinBase(parts[..idx]);
        foreach (var dir in SafeFileWalker.EnumerateDirectories(baseDir))
        {
            if (!FileSystemName.MatchesSimpleExpression(parts[idx], Path.GetFileName(dir), ignoreCase: true))
                continue;
            var rest = new string[parts.Length - idx];
            rest[0] = dir;
            Array.Copy(parts, idx + 1, rest, 1, parts.Length - idx - 1);
            foreach (var r in ExpandParts(rest)) yield return r;
        }
    }

    private static string JoinBase(string[] parts)
    {
        var s = string.Join(Path.DirectorySeparatorChar, parts);
        if (s.Length == 0) return Path.DirectorySeparatorChar.ToString();
        if (s.EndsWith(':')) s += Path.DirectorySeparatorChar; // "C:" → "C:\"
        return s;
    }
}
