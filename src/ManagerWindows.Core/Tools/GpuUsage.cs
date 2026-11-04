namespace ManagerWindows.Core.Tools;

/// <summary>
/// % GPU giống Task Manager từ bộ đếm "\GPU Engine(*)\Utilization Percentage":
/// cộng các tiến trình theo từng engine (3D, Copy, VideoDecode… của từng GPU), lấy engine bận nhất.
/// Tên phiên bản dạng "pid_1234_luid_0x0_0xD1E2_phys_0_eng_0_engtype_3D".
/// </summary>
public static class GpuUsage
{
    public static double Aggregate(IEnumerable<(string Instance, double Value)> samples)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in samples)
        {
            if (value <= 0) continue;
            var key = EngineKey(instance);
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }
        return perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    /// <summary>"luid_…_phys_0_eng_3_engtype_3D" — bỏ phần pid để gộp mọi tiến trình dùng cùng một engine.</summary>
    private static string EngineKey(string instance)
    {
        var i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? instance[i..] : instance;
    }
}
