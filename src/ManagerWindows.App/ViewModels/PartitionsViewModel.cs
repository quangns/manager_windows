using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.App.Views;
using ManagerWindows.Core.Partitions;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

/// <summary>Một ô trên sơ đồ đĩa (phân vùng hoặc vùng chưa phân bổ).</summary>
public sealed partial class SegmentRow : ObservableObject
{
    public SegmentRow(DiskSegment seg)
    {
        Segment = seg;
        var p = seg.Partition;
        Kind = p == null ? "free" : p.IsWindowsCritical && p.Kind == PartitionKind.Data ? "windows" : p.Kind == PartitionKind.Data ? "data" : "system";
        Title = p == null ? Loc.Instance["Part_Unallocated"]
            : p.Letter is { } l ? $"{l}:" + (p.Label != null ? $" {p.Label}" : "")
            : p.Label ?? Loc.Instance["Part_Kind_" + p.Kind];
        SizeText = Fmt.Bytes((long)seg.Size);
        FsText = p == null ? "" : p.FileSystem ?? (p.Kind == PartitionKind.Data ? "RAW" : Loc.Instance["Part_Kind_" + p.Kind]);
        if (p?.FreeBytes is { } free && p.Size > 0)
        {
            UsedRatio = 1 - (double)free / p.Size;
            HasUsage = true;
            UsageText = Loc.F("Part_UsedOf", ("used", Fmt.Bytes((long)(p.Size - Math.Min(free, p.Size)))), ("free", Fmt.Bytes((long)free)));
        }
    }

    public DiskSegment Segment { get; }
    /// <summary>"free" | "windows" | "data" | "system" — chọn màu ô.</summary>
    public string Kind { get; }
    public string Title { get; }
    public string SizeText { get; }
    public string FsText { get; }
    public double UsedRatio { get; }
    public bool HasUsage { get; }
    public string UsageText { get; } = "";
    /// <summary>Độ rộng tương đối trên sơ đồ (căn bậc hai để phân vùng nhỏ vẫn nhìn thấy, đĩa lớn không lấn hết).</summary>
    public double Weight => Math.Sqrt(Segment.Size / (double)DiskLayout.MiB);
    [ObservableProperty] private bool _isSelected;
}

public sealed class DiskMapRow(DiskInfo disk, List<SegmentRow> segments)
{
    public DiskInfo Disk { get; } = disk;
    public List<SegmentRow> Segments { get; } = segments;
    public string Title => Loc.F("Part_DiskTitle", ("n", Disk.Number), ("name", Disk.Name));
    public string Info => string.Join(" · ", new[]
    {
        Fmt.Bytes((long)Disk.Size),
        Disk.Style switch { PartitionStyle.Gpt => "GPT", PartitionStyle.Mbr => "MBR", _ => Loc.Instance["Part_NotInitialized"] },
        Disk.Bus,
        Disk.IsBoot || Disk.IsSystem ? Loc.Instance["Part_SystemDisk"] : "",
        Disk.IsOffline ? Loc.Instance["Part_Offline"] : "",
        Disk.IsReadOnly ? Loc.Instance["Part_ReadOnly"] : "",
    }.Where(s => s.Length > 0));
}

/// <summary>Một nút thao tác cho ô đang chọn; bị khoá thì tooltip giải thích lý do.</summary>
public sealed class PartitionAction(string label, string glyph, bool enabled, string? reason, Func<Task> run, bool danger = false)
{
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;
    public bool IsEnabled { get; } = enabled;
    public string? Reason { get; } = reason;
    public bool IsDanger { get; } = danger;
    public IAsyncRelayCommand Command { get; } = new AsyncRelayCommand(run, () => enabled);
}

/// <summary>
/// Quản lý phân vùng (tương tự MiniTool Partition Wizard, phần Windows hỗ trợ an toàn): sơ đồ đĩa, thu nhỏ/mở rộng,
/// tạo, xoá, format, đổi ký tự, đổi nhãn, kiểm tra lỗi, khởi tạo đĩa mới. Thay đổi cần quyền Administrator:
/// app thường mở một cửa sổ riêng chạy quyền Admin (--partitions).
/// </summary>
public sealed partial class PartitionsViewModel : ViewModelBase
{
    public ObservableCollection<DiskMapRow> Disks { get; } = new();
    public ObservableCollection<PartitionAction> Actions { get; } = new();

    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public bool NotElevated => !IsElevated;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] private SegmentRow? _selected;
    [ObservableProperty] private string _loadError = "";
    [ObservableProperty] private bool _hasLoadError;
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailText = "";
    /// <summary>Có thao tác dời phân vùng đang dở (nhật ký còn): phải tiếp tục trước khi dùng phân vùng đó.</summary>
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private string _pendingText = "";
    public bool HasSelection => Selected != null;

    private static readonly char? WindowsLetter = Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } r ? char.ToUpperInvariant(r[0]) : null;
    private static readonly char? AppLetter = Environment.ProcessPath is { Length: > 1 } p && p[1] == ':' ? char.ToUpperInvariant(p[0]) : null;

    [RelayCommand]
    public Task RefreshAsync() => RunBusyAsync(async () =>
    {
        var keep = Selected?.Segment;
        if (PartitionMover.PendingMove() is { } j)
        {
            HasPending = true;
            PendingText = Loc.F("Part_PendingText", ("part", j.Letter is { } l ? $"{l}:" : $"#{j.Partition}"), ("disk", j.Disk),
                ("pct", (j.Length > 0 ? j.Done * 100.0 / j.Length : 0).ToString("0")));
        }
        else HasPending = false;
        List<DiskInfo> disks;
        try
        {
            disks = await Task.Run(StorageApi.Load);
            HasLoadError = false;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("partition", ex);
            LoadError = Loc.F("Part_LoadFailed", ("error", ex.Message));
            HasLoadError = true;
            Status = "";
            return;
        }
        Disks.Clear();
        foreach (var d in disks) Disks.Add(new DiskMapRow(d, DiskLayout.Segments(d).Select(s => new SegmentRow(s)).ToList()));
        // Giữ lựa chọn cũ nếu ô đó vẫn còn (cùng đĩa, cùng vị trí).
        var again = keep == null ? null : Disks.SelectMany(d => d.Segments)
            .FirstOrDefault(s => s.Segment.Disk.Number == keep.Disk.Number && s.Segment.Offset == keep.Offset);
        Select(again);
        Status = Loc.F("Part_Loaded", ("count", disks.Count));
    });

    [RelayCommand]
    private void Select(SegmentRow? row)
    {
        if (Selected != null) Selected.IsSelected = false;
        Selected = row;
        if (row != null) row.IsSelected = true;
        BuildDetails();
    }

    private void BuildDetails()
    {
        Actions.Clear();
        if (Selected is not { } row) { DetailTitle = DetailText = ""; return; }
        var seg = row.Segment;
        var p = seg.Partition;
        DetailTitle = p == null ? Loc.F("Part_FreeOn", ("n", seg.Disk.Number)) : Loc.F("Part_PartitionOn", ("title", row.Title), ("n", seg.Disk.Number), ("num", p.Number));
        var lines = new List<string> { Loc.F("Part_DetailSize", ("size", row.SizeText)) };
        if (p != null)
        {
            lines.Add(Loc.F("Part_DetailFs", ("fs", row.FsText), ("kind", Loc.Instance["Part_Kind_" + p.Kind])));
            if (row.HasUsage) lines.Add(row.UsageText);
            if (p.IsWindowsCritical) lines.Add(Loc.Instance[p.IsBoot ? "Part_FlagBoot" : "Part_FlagSystem"]);
        }
        lines.Add(Loc.F("Part_DetailOffset", ("offset", Fmt.Bytes((long)seg.Offset))));
        DetailText = string.Join("\n", lines);

        void Add(PartitionOp? op, string labelKey, string glyph, Func<Task> run, bool danger = false)
        {
            var check = op is { } o ? DiskLayout.Check(o, seg, WindowsLetter, AppLetter) : OpCheck.Ok;
            if (check.Allowed && op != null && !IsElevated) check = OpCheck.Deny("Part_Deny_NeedAdmin");
            Actions.Add(new PartitionAction(Loc.Instance[labelKey], glyph, check.Allowed,
                check.ReasonKey != null ? Loc.Instance[check.ReasonKey] : null, run, danger));
        }

        void AddCustom(OpCheck check, string labelKey, string glyph, Func<Task> run)
        {
            if (check.Allowed && !IsElevated) check = OpCheck.Deny("Part_Deny_NeedAdmin");
            if (check.Allowed && HasPending) check = OpCheck.Deny("Part_Deny_Pending");
            Actions.Add(new PartitionAction(Loc.Instance[labelKey], glyph, check.Allowed,
                check.ReasonKey != null ? Loc.Instance[check.ReasonKey] : null, run));
        }

        if (seg.Disk.Style == PartitionStyle.Raw)
        {
            Add(PartitionOp.Initialize, "Part_ActInitialize", "", InitializeAsync);
            return;
        }
        if (p == null)
        {
            Add(PartitionOp.Create, "Part_ActCreate", "", CreateAsync);
            return;
        }
        Add(PartitionOp.Resize, "Part_ActResize", "", ResizeAsync);
        AddCustom(MovePlanner.CanMove(seg, WindowsLetter, AppLetter), "Part_ActMove", "\uE7C2", MoveAsync);
        var left = MovePlanner.CanMove(seg, WindowsLetter, AppLetter);
        if (left.Allowed && MovePlanner.Bounds(seg).Lo + DiskLayout.MinFree > p.Offset) left = OpCheck.Deny("Part_Plan_NoSpaceBefore");
        AddCustom(left, "Part_ActExtendLeft", "\uE76B", ExtendLeftAsync);
        var neighbors = MergeSources(seg);
        AddCustom(neighbors.Count > 0 ? OpCheck.Ok : OpCheck.Deny("Part_Deny_NoNeighbor"), "Part_ActMerge", "\uE71D", MergeAsync);
        Add(PartitionOp.ChangeLetter, "Part_ActLetter", "", ChangeLetterAsync);
        Add(PartitionOp.Label, "Part_ActLabel", "", LabelAsync);
        Add(PartitionOp.Check, "Part_ActCheck", "", CheckAsync);
        if (p.Letter != null) Add(null, "Part_ActOpen", "", () => { Open(p.Letter.Value); return Task.CompletedTask; });
        Add(PartitionOp.Format, "Part_ActFormat", "", FormatAsync, danger: true);
        Add(PartitionOp.Delete, "Part_ActDelete", "", DeleteAsync, danger: true);
    }

    // ---------------- Mở bản quyền Admin ----------------

    [RelayCommand]
    private async Task OpenElevatedAsync()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--partitions") { UseShellExecute = true, Verb = "runas" });
            if (proc == null) return;
            Status = Loc.Instance["Part_ElevatedOpened"];
            await proc.WaitForExitAsync();
            await RefreshAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // người dùng bấm No ở hộp thoại UAC
        {
            Status = Loc.Instance["Part_UacCancelled"];
        }
    }

    // ---------------- Thao tác ----------------

    private string Describe(DiskSegment seg) =>
        seg.Partition is { } p
            ? $"{(p.Letter is { } l ? l + ": " : "")}{p.Label ?? ""} ({Fmt.Bytes((long)p.Size)}, {Loc.F("Part_DiskShort", ("n", seg.Disk.Number))} #{p.Number})".Replace("  ", " ")
            : $"{Loc.Instance["Part_Unallocated"]} ({Fmt.Bytes((long)seg.Size)}, {Loc.F("Part_DiskShort", ("n", seg.Disk.Number))})";

    /// <summary>Chạy thao tác ở luồng nền, ghi log, báo kết quả, rồi đọc lại sơ đồ đĩa.</summary>
    private Task Run(string what, Action work) => RunBusyAsync(async () =>
    {
        Log.Info("partition", "BẮT ĐẦU " + what);
        try
        {
            await Task.Run(work);
            Log.Info("partition", "XONG " + what);
            Status = Loc.F("Part_Done", ("what", what));
        }
        catch (Exception ex) when (ex is StorageException or System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("partition", ex);
            Status = Loc.F("Part_Failed", ("what", what), ("error", ex.Message));
            Info(Status);
        }
        Busy = false;
        var result = Status;
        await RefreshAsync();
        Status = result; // giữ thông báo kết quả thay vì "đã đọc N đĩa"
    });

    private async Task ResizeAsync()
    {
        if (Selected?.Segment is not { Partition: { } p } seg) return;
        (ulong Min, ulong Max) range;
        try { range = await Task.Run(() => StorageApi.SupportedSize(p.Disk, p.Number)); }
        catch (Exception ex) when (ex is StorageException or System.Management.ManagementException)
        {
            Info(Loc.F("Part_Failed", ("what", Loc.Instance["Part_ActResize"]), ("error", ex.Message)));
            return;
        }
        var dlg = new PartitionDialog(Loc.Instance["Part_ActResize"], Loc.F("Part_ResizeIntro", ("part", Describe(seg))));
        var size = dlg.AddSize(Loc.Instance["Part_NewSize"], p.Size, range.Min, range.Max);
        dlg.AddNote(Loc.Instance["Part_ResizeNote"]);
        if (dlg.ShowDialog() != true || size.Value is not { } bytes || bytes == p.Size) return;
        await Run(Loc.F("Part_WhatResize", ("part", Describe(seg)), ("size", Fmt.Bytes((long)bytes))), () => StorageApi.Resize(p.Disk, p.Number, bytes));
    }

    // ---------------- Dời / mở rộng sang trái / gộp (chép sector) ----------------

    /// <summary>Phân vùng dữ liệu liền kề (chỉ cách nhau vùng trống) có thể gộp vào phân vùng đang chọn.</summary>
    private static List<DiskSegment> MergeSources(DiskSegment target)
    {
        var segs = DiskLayout.Segments(target.Disk);
        var i = segs.FindIndex(s => s.Partition?.Number == target.Partition?.Number);
        var list = new List<DiskSegment>();
        if (i < 0) return list;
        foreach (var dir in new[] { -1, 1 })
            for (var j = i + dir; j >= 0 && j < segs.Count; j += dir)
            {
                if (segs[j].IsFree) continue;
                if (MovePlanner.Merge(target, segs[j], "x").Error is null) list.Add(segs[j]);
                break;
            }
        return list;
    }

    private static string Describe(PlanStep s) => s.Kind switch
    {
        StepKind.Shrink => Loc.F("Part_PlanShrink", ("n", s.Partition), ("size", Fmt.Bytes((long)s.Size))),
        StepKind.Move => Loc.F("Part_PlanMove", ("n", s.Partition), ("offset", Fmt.Bytes((long)s.Offset)), ("size", Fmt.Bytes((long)s.Size))),
        StepKind.Extend => s.Size == 0 ? Loc.F("Part_PlanExtendMax", ("n", s.Partition)) : Loc.F("Part_PlanExtend", ("n", s.Partition), ("size", Fmt.Bytes((long)s.Size))),
        StepKind.CopyFiles => Loc.F("Part_PlanCopy", ("from", s.Source!), ("to", s.Target!), ("size", Fmt.Bytes((long)s.Size))),
        StepKind.Delete => Loc.F("Part_PlanDelete", ("n", s.Partition), ("size", Fmt.Bytes((long)s.Size))),
        _ => s.Kind.ToString(),
    };

    /// <summary>Rủi ro cụ thể của kế hoạch: dời (chép sector), gộp (chép file + xoá), phân vùng Recovery…</summary>
    private static List<string> RisksFor(IReadOnlyCollection<PlanStep> steps, DiskSegment seg)
    {
        var keys = new List<string>();
        var move = steps.Any(s => s.Kind == StepKind.Move);
        var merge = steps.Any(s => s.Kind == StepKind.CopyFiles);
        if (merge) keys.AddRange(["Risk_MergeDelete", "Risk_MergeSkip", "Risk_MergeApps", "Risk_MergeSpace"]);
        if (move) keys.AddRange(["Risk_Power", "Risk_BadSector", "Risk_Locked", "Risk_Letter"]);
        if (move && seg.Partition?.Kind == PartitionKind.Recovery) keys.Add("Risk_Recovery");
        if (steps.Any(s => s.Kind is StepKind.Shrink or StepKind.Extend)) keys.Add("Risk_Resize");
        keys.Add("Risk_Untested");
        return keys.Select(k => Loc.Instance[k]).ToList();
    }

    /// <summary>Xác nhận kế hoạch (liệt kê từng bước, bắt tick đã sao lưu + gõ chữ xác nhận) rồi chạy với cửa sổ tiến độ.</summary>
    private async Task ConfirmAndRunAsync(string title, string intro, List<PlanStep> steps)
    {
        var lines = string.Join("\n", steps.Select((s, i) => $"{i + 1}. {Describe(s)}"));
        var dlg = new PartitionDialog(title, $"{intro}\n\n{lines}", danger: true);
        dlg.AddRisks(RisksFor(steps, Selected!.Segment));
        if (steps.Any(s => s.Kind == StepKind.Move)) dlg.AddNote(Loc.Instance["Part_MoveNote"]);
        dlg.RequireCheck(Loc.Instance["Part_BackupCheck"]);
        dlg.RequireTyping(Loc.Instance["Part_AgreeWord"]);
        if (dlg.ShowDialog() != true) return;
        await RunPlanAsync(title, steps, resume: false);
    }

    private Task RunPlanAsync(string title, List<PlanStep>? steps, bool resume) => RunBusyAsync(async () =>
    {
        var win = new PartitionProgressWindow(title);
        var progress = new Progress<PlanProgress>(win.Report);
        var token = win.Token;
        Log.Info("partition", "BẮT ĐẦU " + title + (steps == null ? "" : ": " + string.Join(" | ", steps)));
        win.Show();
        try
        {
            await Task.Run(() =>
            {
                if (resume) PartitionMover.Resume(progress, token);
                else PartitionMover.Run(steps!, progress, token);
            });
            Log.Info("partition", "XONG " + title);
            Status = Loc.F("Part_Done", ("what", title));
        }
        catch (OperationCanceledException)
        {
            Log.Info("partition", "TẠM DỪNG " + title);
            Status = Loc.Instance["Part_Paused"];
            Info(Status);
        }
        catch (Exception ex) when (ex is IOException or StorageException or InvalidOperationException or UnauthorizedAccessException
                                       or Win32Exception or System.Management.ManagementException)
        {
            Log.Error("partition", ex);
            Status = Loc.F("Part_Failed", ("what", title), ("error", ex.Message))
                     + (PartitionMover.PendingMove() != null ? "\n\n" + Loc.Instance["Part_FailedResumable"] : "");
            Info(Status);
        }
        finally { win.Finish(); }
        Busy = false;
        var result = Status;
        await RefreshAsync();
        Status = result;
    });

    [RelayCommand]
    private Task ResumeAsync()
    {
        if (!IsElevated) return OpenElevatedAsync();
        if (!Confirm(Loc.F("Part_ResumeAsk", ("text", PendingText)))) return Task.CompletedTask;
        return RunPlanAsync(Loc.Instance["Part_ActResume"], null, resume: true);
    }

    private async Task MoveAsync()
    {
        if (Selected?.Segment is not { Partition: { } p } seg) return;
        (ulong Min, ulong Max) range;
        try { range = await Task.Run(() => StorageApi.SupportedSize(p.Disk, p.Number)); }
        catch (Exception ex) when (ex is StorageException or System.Management.ManagementException) { Info(ex.Message); return; }
        var (lo, hi) = MovePlanner.Bounds(seg);
        var room = hi - lo;
        var dlg = new PartitionDialog(Loc.Instance["Part_ActMove"], Loc.F("Part_MoveIntro", ("part", Describe(seg)), ("room", Fmt.Bytes((long)room))));
        var before = dlg.AddSize(Loc.Instance["Part_SpaceBefore"], p.Offset - lo, 0, room - range.Min);
        var size = dlg.AddSize(Loc.Instance["Part_Size"], p.Size, range.Min, room);
        dlg.AddValidator(() => before.Value + size.Value > room
            ? Loc.F("Part_SizeOutOfRange", ("min", Fmt.Bytes((long)range.Min)), ("max", Fmt.Bytes((long)(room - (before.Value ?? 0))))) : null);
        dlg.AddNote(Loc.Instance["Part_MoveHint"]);
        if (dlg.ShowDialog() != true || before.Value is not { } b || size.Value is not { } sz) return;
        var (steps, err) = MovePlanner.MoveResize(seg, lo + b, sz, range.Min);
        if (err != null) { Info(Loc.Instance[err]); return; }
        await ConfirmAndRunAsync(Loc.Instance["Part_ActMove"], Describe(seg), steps!);
    }

    private async Task ExtendLeftAsync()
    {
        if (Selected?.Segment is not { } seg) return;
        var (steps, err) = MovePlanner.ExtendLeft(seg);
        if (err != null) { Info(Loc.Instance[err]); return; }
        await ConfirmAndRunAsync(Loc.Instance["Part_ActExtendLeft"], Describe(seg), steps!);
    }

    private async Task MergeAsync()
    {
        if (Selected?.Segment is not { Partition: not null } seg) return;
        var sources = MergeSources(seg);
        if (sources.Count == 0) return;
        var dlg = new PartitionDialog(Loc.Instance["Part_ActMerge"], Loc.F("Part_MergeIntro", ("part", Describe(seg))));
        var source = dlg.AddChoice(Loc.Instance["Part_MergeSource"],
            sources.Select(s => new Option<string>(s.Partition!.Number.ToString(), Describe(s))), sources[0].Partition!.Number.ToString());
        var folder = dlg.AddText(Loc.Instance["Part_MergeFolder"], "");
        void SuggestFolder()
        {
            var src = sources.FirstOrDefault(s => s.Partition!.Number.ToString() == source.Value)?.Partition;
            if (src != null) folder.Text = Loc.F("Part_MergeFolderDefault", ("letter", src.Letter?.ToString() ?? src.Number.ToString()));
        }
        SuggestFolder();
        source.Changed += SuggestFolder;
        dlg.AddValidator(() => folder.Text.Trim().Length == 0 || folder.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? Loc.Instance["Part_BadFolder"] : null);
        dlg.AddNote(Loc.Instance["Part_MergeNote"]);
        if (dlg.ShowDialog() != true) return;
        var srcSeg = sources.First(s => s.Partition!.Number.ToString() == source.Value);
        var (steps, err) = MovePlanner.Merge(seg, srcSeg, folder.Text.Trim());
        if (err != null) { Info(Loc.Instance[err]); return; }
        await ConfirmAndRunAsync(Loc.Instance["Part_ActMerge"], Loc.F("Part_MergeSummary", ("source", Describe(srcSeg)), ("target", Describe(seg))), steps!);
    }

    private static readonly string[] FileSystems = ["NTFS", "exFAT", "FAT32"];

    private List<char> UsedLetters() =>
        Disks.SelectMany(d => d.Disk.Partitions).Where(x => x.Letter != null).Select(x => x.Letter!.Value)
            .Concat(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0]))).ToList();

    private async Task CreateAsync()
    {
        if (Selected?.Segment is not { IsFree: true } seg) return;
        // Căn đầu phân vùng theo 1 MB (chuẩn của Windows, tốt cho SSD).
        var offset = (seg.Offset + DiskLayout.MiB - 1) / DiskLayout.MiB * DiskLayout.MiB;
        var max = (seg.Offset + seg.Size - offset) / DiskLayout.MiB * DiskLayout.MiB;
        var dlg = new PartitionDialog(Loc.Instance["Part_ActCreate"], Loc.F("Part_CreateIntro", ("part", Describe(seg))));
        var size = dlg.AddSize(Loc.Instance["Part_Size"], max, DiskLayout.MinFree, max);
        var fs = dlg.AddChoice(Loc.Instance["Part_FileSystem"], FileSystems.Select(f => new Option<string>(f, f)), "NTFS");
        var label = dlg.AddText(Loc.Instance["Part_LabelField"], Loc.Instance["Part_DefaultLabel"]);
        var letters = DiskLayout.FreeLetters(UsedLetters());
        var letter = dlg.AddChoice(Loc.Instance["Part_Letter"],
            letters.Select(c => new Option<string>(c.ToString(), $"{c}:")).Append(new Option<string>("", Loc.Instance["Part_NoLetter"])),
            letters.Count > 0 ? letters[0].ToString() : "");
        if (dlg.ShowDialog() != true || size.Value is not { } bytes) return;
        if (fs.Value == "FAT32" && bytes > 32 * DiskLayout.GiB)
        {
            Info(Loc.Instance["Part_Fat32Limit"]);
            return;
        }
        char? l = letter.Value is { Length: 1 } s ? s[0] : null;
        await Run(Loc.F("Part_WhatCreate", ("size", Fmt.Bytes((long)bytes)), ("disk", seg.Disk.Number)),
            () => StorageApi.Create(seg.Disk.Number, offset, bytes, l, fs.Value!, label.Text.Trim()));
    }

    private async Task DeleteAsync()
    {
        if (Selected?.Segment is not { Partition: { } p } seg) return;
        var dlg = new PartitionDialog(Loc.Instance["Part_ActDelete"], Loc.F("Part_DeleteWarn", ("part", Describe(seg))), danger: true);
        dlg.AddRisks([Loc.Instance["Risk_Erase"], Loc.Instance["Risk_EraseApps"], Loc.Instance["Risk_WrongDisk"]]);
        dlg.RequireTyping(Loc.Instance["Part_ConfirmWord"]);
        if (dlg.ShowDialog() != true) return;
        await Run(Loc.F("Part_WhatDelete", ("part", Describe(seg))), () => StorageApi.Delete(p.Disk, p.Number));
    }

    private async Task FormatAsync()
    {
        if (Selected?.Segment is not { Partition: { } p } seg) return;
        var dlg = new PartitionDialog(Loc.Instance["Part_ActFormat"], Loc.F("Part_FormatWarn", ("part", Describe(seg))), danger: true);
        dlg.AddRisks([Loc.Instance["Risk_Erase"], Loc.Instance["Risk_EraseApps"], Loc.Instance["Risk_WrongDisk"]]);
        var fs = dlg.AddChoice(Loc.Instance["Part_FileSystem"], FileSystems.Select(f => new Option<string>(f, f)), p.FileSystem is "exFAT" or "FAT32" ? p.FileSystem : "NTFS");
        var label = dlg.AddText(Loc.Instance["Part_LabelField"], p.Label ?? Loc.Instance["Part_DefaultLabel"]);
        dlg.RequireTyping(Loc.Instance["Part_ConfirmWord"]);
        if (dlg.ShowDialog() != true) return;
        if (fs.Value == "FAT32" && p.Size > 32 * DiskLayout.GiB)
        {
            Info(Loc.Instance["Part_Fat32Limit"]);
            return;
        }
        await Run(Loc.F("Part_WhatFormat", ("part", Describe(seg)), ("fs", fs.Value!)), () => StorageApi.Format(p.Disk, p.Number, fs.Value!, label.Text.Trim()));
    }

    private async Task ChangeLetterAsync()
    {
        if (Selected?.Segment is not { Partition: { } p } seg) return;
        var letters = DiskLayout.FreeLetters(UsedLetters());
        var options = letters.Select(c => new Option<string>(c.ToString(), $"{c}:")).ToList();
        if (p.Letter is { } cur) options.Insert(0, new Option<string>(cur.ToString(), $"{cur}: ({Loc.Instance["Part_Current"]})"));
        options.Add(new Option<string>("", Loc.Instance["Part_RemoveLetter"]));
        var dlg = new PartitionDialog(Loc.Instance["Part_ActLetter"], Loc.F("Part_LetterIntro", ("part", Describe(seg))));
        var letter = dlg.AddChoice(Loc.Instance["Part_Letter"], options, p.Letter?.ToString() ?? "");
        dlg.AddNote(Loc.Instance["Part_LetterNote"]);
        if (dlg.ShowDialog() != true) return;
        char? n = letter.Value is { Length: 1 } s ? s[0] : null;
        if (n == p.Letter) return;
        await Run(Loc.F("Part_WhatLetter", ("part", Describe(seg)), ("letter", n is { } c ? c + ":" : "—")),
            () => StorageApi.SetLetter(p.Disk, p.Number, p.Letter, n));
    }

    private async Task LabelAsync()
    {
        if (Selected?.Segment is not { Partition: { } p } seg) return;
        var dlg = new PartitionDialog(Loc.Instance["Part_ActLabel"], Describe(seg));
        var label = dlg.AddText(Loc.Instance["Part_LabelField"], p.Label ?? "");
        if (dlg.ShowDialog() != true) return;
        var text = label.Text.Trim();
        await Run(Loc.F("Part_WhatLabel", ("part", Describe(seg)), ("label", text)), () => StorageApi.SetLabel(p.Disk, p.Number, text));
    }

    /// <summary>Kiểm tra lỗi bằng chkdsk /scan (chạy trực tuyến, không cần khởi động lại) trong cửa sổ dòng lệnh.</summary>
    private Task CheckAsync()
    {
        if (Selected?.Segment.Partition is not { Letter: { } l }) { Info(Loc.Instance["Part_NeedLetter"]); return Task.CompletedTask; }
        Log.Info("partition", $"chkdsk {l}: /scan");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k chkdsk {l}: /scan") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private static void Open(char letter) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"{letter}:\\") { UseShellExecute = false });

    private async Task InitializeAsync()
    {
        if (Selected?.Segment is not { } seg) return;
        var dlg = new PartitionDialog(Loc.Instance["Part_ActInitialize"], Loc.F("Part_InitIntro", ("disk", seg.Disk.Number), ("name", seg.Disk.Name), ("size", Fmt.Bytes((long)seg.Disk.Size))));
        var style = dlg.AddChoice(Loc.Instance["Part_Style"],
            [new Option<string>("GPT", Loc.Instance["Part_StyleGpt"]), new Option<string>("MBR", Loc.Instance["Part_StyleMbr"])], "GPT");
        if (dlg.ShowDialog() != true) return;
        var s = style.Value == "MBR" ? PartitionStyle.Mbr : PartitionStyle.Gpt;
        await Run(Loc.F("Part_WhatInit", ("disk", seg.Disk.Number), ("style", s.ToString().ToUpperInvariant())), () => StorageApi.Initialize(seg.Disk.Number, s));
    }
}
