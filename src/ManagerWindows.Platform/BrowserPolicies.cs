using System.Runtime.Versioning;
using ManagerWindows.Core.Monitoring;
using Microsoft.Win32;

namespace ManagerWindows.Platform;

/// <summary>
/// Đọc/ghi chính sách Memory Saver của trình duyệt dưới HKLM\SOFTWARE\Policies. Đọc được với quyền người dùng;
/// ghi cần quyền SYSTEM/Administrator (do dịch vụ nền làm). Luôn dùng nhánh registry 64-bit.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserPolicies
{
    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static PolicySnapshot Read(TabSleepBrowser browser)
    {
        var path = BrowserCatalog.Get(browser).PolicyKey;
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(path);
        if (key == null) return PolicySnapshot.Empty;
        using var ex = key.OpenSubKey(BrowserPolicyPlanner.ExceptionsKey);
        return new PolicySnapshot(
            key.GetValue(BrowserPolicyPlanner.EnabledValue) as int?,
            key.GetValue(BrowserPolicyPlanner.SavingsValue) as int?,
            ex != null && ex.ValueCount > 0);
    }

    public static void Apply(IEnumerable<PolicyOp> ops)
    {
        using var hklm = Hklm();
        foreach (var op in ops)
        {
            switch (op)
            {
                case SetDwordOp d:
                    using (var k = hklm.CreateSubKey(d.Key, writable: true)) k.SetValue(d.Name, d.Value, RegistryValueKind.DWord);
                    break;
                case SetStringOp s:
                    using (var k = hklm.CreateSubKey(s.Key, writable: true)) k.SetValue(s.Name, s.Value, RegistryValueKind.String);
                    break;
                case DeleteValueOp v:
                    using (var k = hklm.OpenSubKey(v.Key, writable: true)) k?.DeleteValue(v.Name, throwOnMissingValue: false);
                    break;
                case DeleteKeyOp k:
                    hklm.DeleteSubKeyTree(k.Key, throwOnMissingSubKey: false);
                    break;
            }
        }
    }

    /// <summary>Trình duyệt có trên máy không (kiểm tra các nơi cài thường gặp, cả cài cho mọi người lẫn cho riêng user).</summary>
    public static bool IsInstalled(TabSleepBrowser browser) =>
        BrowserCatalog.Get(browser).InstallPaths.Any(p => File.Exists(Environment.ExpandEnvironmentVariables(p)));
}
