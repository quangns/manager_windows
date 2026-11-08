using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ManagerWindows.Core.Monitoring;
using Microsoft.Win32;

namespace ManagerWindows.Platform;

/// <summary>
/// Đọc/ghi chính sách Memory Saver của trình duyệt dưới HKLM\SOFTWARE\Policies. Đọc được với quyền người dùng;
/// ghi cần quyền SYSTEM/Administrator: dịch vụ nền (bản cài đặt) hoặc chính app mở lại bằng quyền Admin (bản portable).
/// Luôn dùng nhánh registry 64-bit.
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

    /// <summary>
    /// Danh sách mục chính sách do Manager Windows đặt (theo trình duyệt), dùng chung cho dịch vụ nền (bản cài đặt)
    /// và app chạy quyền Admin (bản portable). Khi tắt/gỡ chỉ xoá đúng những mục này.
    /// </summary>
    public static string OwnershipFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ManagerWindows", "browser-policies.json");

    /// <summary>
    /// Ghi chính sách theo yêu cầu (cần quyền SYSTEM/Administrator). Kiểm tra lại yêu cầu ở đây vì người gửi có thể là bất kỳ ai.
    /// <paramref name="legacyOwned"/>: danh sách cũ (v0.11.0 lưu trong service.json) để gộp vào file chung.
    /// </summary>
    public static (string? Error, List<TabSleepBrowserResult> Results) ApplyRequest(TabSleepRequest r,
        IReadOnlyDictionary<string, List<string>>? legacyOwned = null)
    {
        var error = BrowserPolicyPlanner.Validate(r, out var browsers);
        if (error != null) return (error, []);
        // Dịch vụ nền và app quyền Admin có thể chạy cùng lúc: khoá giữa các tiến trình.
        using var mutex = new Mutex(false, @"Global\ManagerWindows.BrowserPolicies");
        bool locked;
        try { locked = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { locked = true; } // tiến trình trước thoát giữa chừng: vẫn có khoá
        if (!locked) return ("Đang có tiến trình khác ghi chính sách trình duyệt, thử lại sau", []);
        try
        {
            var owned = LoadOwned();
            foreach (var (k, v) in legacyOwned ?? new Dictionary<string, List<string>>())
                owned[k] = [.. (owned.GetValueOrDefault(k) ?? []).Union(v)];
            var results = new List<TabSleepBrowserResult>();
            foreach (var info in BrowserCatalog.All)
            {
                var name = info.Browser.ToString();
                var mine = new HashSet<string>(owned.GetValueOrDefault(name) ?? [], StringComparer.Ordinal);
                var enable = r.Enabled && browsers.Contains(info.Browser);
                var plan = BrowserPolicyPlanner.Build(info.Browser, enable, (TabSleepSavings)r.Savings, r.Exceptions ?? [], Read(info.Browser), mine);
                try { Apply(plan.Ops); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    SaveOwned(owned);
                    return ($"Không ghi được chính sách {info.DisplayName}: {ex.Message}", results);
                }
                if (plan.Owned.Count > 0) owned[name] = plan.Owned.Order().ToList();
                else owned.Remove(name);
                SaveOwned(owned); // lưu sau từng trình duyệt: lỗi giữa chừng vẫn biết mục nào do app đặt
                results.Add(new TabSleepBrowserResult(name, enable && plan.Owned.Contains(BrowserPolicyPlanner.EnabledValue), plan.Conflicts));
            }
            return (null, results);
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>
    /// Đọc file sở hữu. File không do SYSTEM/Administrators tạo (người dùng thường tạo sẵn để giả mạo) thì bỏ qua:
    /// khi đó mọi chính sách đang có được coi là của quản trị viên và giữ nguyên.
    /// </summary>
    private static Dictionary<string, List<string>> LoadOwned()
    {
        try
        {
            if (!File.Exists(OwnershipFile)) return new();
            var owner = new FileInfo(OwnershipFile).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
                return new();
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(OwnershipFile)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>
    /// Ghi file sở hữu, chỉ SYSTEM và Administrators được sửa (người dùng thường chỉ đọc), để không ai "nhận" chính sách
    /// của quản trị viên là do app đặt rồi khiến app xoá mất.
    /// </summary>
    private static void SaveOwned(Dictionary<string, List<string>> owned)
    {
        var dir = Path.GetDirectoryName(OwnershipFile)!;
        Directory.CreateDirectory(dir);
        var tmp = OwnershipFile + ".tmp";
        File.Delete(tmp); // file tạm cũ (có thể do người khác tạo): xoá rồi tạo mới để chủ sở hữu là tiến trình quyền cao này
        File.WriteAllText(tmp, JsonSerializer.Serialize(owned, new JsonSerializerOptions { WriteIndented = true }));
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(tmp).SetAccessControl(sec);
        File.Move(tmp, OwnershipFile, overwrite: true);
    }

    /// <summary>Trình duyệt có trên máy không (kiểm tra các nơi cài thường gặp, cả cài cho mọi người lẫn cho riêng user).</summary>
    public static bool IsInstalled(TabSleepBrowser browser) =>
        BrowserCatalog.Get(browser).InstallPaths.Any(p => File.Exists(Environment.ExpandEnvironmentVariables(p)));
}
