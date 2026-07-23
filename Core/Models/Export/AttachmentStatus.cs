namespace Core.Models.Export;

/// <summary>
/// realtime.attachments.status（smallint）。
/// 与 Realtime 约定一致：
/// 0=Ticketed, 1=Confirmed, 2=Bound, 3=Abandoned,
/// 4=Uploaded, 5=Scanning, 6=Rejected。
/// 生命周期：Ticketed → Uploaded → Scanning → Confirmed → Bound（或 Rejected / Abandoned）。
/// </summary>
public enum AttachmentStatus : short
{
    Ticketed = 0,
    Confirmed = 1,
    Bound = 2,
    Abandoned = 3,
    /// <summary>对象已落盘，尚未进入内容扫描。</summary>
    Uploaded = 4,
    /// <summary>内容扫描中；禁止绑定与下载。</summary>
    Scanning = 5,
    /// <summary>扫描失败/拒绝；禁止绑定与下载。</summary>
    Rejected = 6,
}
