using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NecrologistMascot.Services;

/// <summary>一套皮肤的元信息。</summary>
public sealed class SkinEntry
{
    /// <summary>目录名，同时也是 .atlas / .skel 的文件名前缀。</summary>
    public string Id { get; init; } = "";

    /// <summary>界面上显示的名字（一定有值，不会为 null）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>皮肤目录；目录不存在时为 null。</summary>
    public string? Dir { get; init; }

    /// <summary>.atlas 与 .skel 齐全，真的能加载。</summary>
    public bool IsUsable { get; init; }
}

/// <summary>
/// 皮肤目录扫描（**纯目录扫描，没有任何硬编码皮肤列表**）。
///
/// 约定：Assets/characters/&lt;id&gt;/ 下放同名的 &lt;id&gt;.atlas、&lt;id&gt;.skel 和图集，
/// 该目录即为一套皮肤。放一个 display.txt（UTF-8，首行）可以自定义显示名。
///
/// 往 Assets/characters 里丢一个新目录，重启后就会出现在设置面板与托盘菜单里，
/// 不需要改一行代码。
///
/// 显示名三级兜底（顺序固定）：
///   ① config.json 里 SkinNames 给的名字（用户手动指定，优先级最高）
///   ② 目录里的 display.txt 首行
///   ③ 目录名本身
/// </summary>
public static class SkinCatalog
{
    /// <summary>配置里给皮肤起的名字（id → 名字）。由 ConfigStore.Load 在任何人读 All 之前注入。</summary>
    private static IReadOnlyDictionary<string, string> _configNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static List<SkinEntry>? _cache;

    /// <summary>当前可用的全部皮肤（可用优先，其次按 id 升序）。</summary>
    public static IReadOnlyList<SkinEntry> All => _cache ??= Scan();

    /// <summary>
    /// 注入配置里的皮肤名。必须在第一次访问 <see cref="All"/> 之前调用，
    /// 否则缓存已经按旧名字扫好了（所以这里顺手清缓存）。
    /// </summary>
    public static void SetConfiguredNames(IReadOnlyDictionary<string, string>? names)
    {
        _configNames = names ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _cache = null;
    }

    /// <summary>丢弃缓存，下次访问 All 时重新扫描。</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>皮肤显示名（走三级兜底；不在列表里也能现算）。</summary>
    public static string DisplayNameOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";

        var hit = All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit.DisplayName;

        // 不在缓存里（比如配置里存了已删掉的皮肤），现场算一个
        string? dir = DirOf(id);
        return dir != null ? ResolveName(id, dir) : id;
    }

    /// <summary>某套皮肤的目录；找不到返回 null。</summary>
    public static string? DirOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (string root in RootCandidates())
        {
            string dir = Path.Combine(root, id);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    /// <summary>characters 根目录的候选路径（程序目录优先，其次是开发时的源码目录）。</summary>
    public static IEnumerable<string> RootCandidates()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Assets", "characters");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "characters"));
    }

    private static List<SkinEntry> Scan()
    {
        // id → 目录。同名时以先找到的为准（程序目录优先于源码目录）
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in RootCandidates())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string id = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!found.ContainsKey(id)) found[id] = dir;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skin] 扫描目录失败 {root}", ex);
            }
        }

        var list = new List<SkinEntry>(found.Count);
        foreach (var kv in found)
            list.Add(Make(kv.Key, ResolveName(kv.Key, kv.Value), kv.Value));

        // 可用优先；同组内按 id 升序（保证「第一套可用皮肤」= 默认皮肤 303701）
        list.Sort((a, b) =>
        {
            if (a.IsUsable != b.IsUsable) return a.IsUsable ? -1 : 1;
            return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        Logger.Log($"[Skin] 扫描到 {list.Count} 套皮肤（可用 {list.Count(e => e.IsUsable)} 套）："
                 + string.Join(" | ", list.Select(e => $"{e.Id}={e.DisplayName}{(e.IsUsable ? "" : "(缺资源)")}")));
        return list;
    }

    private static SkinEntry Make(string id, string name, string? dir)
    {
        bool usable = false;
        if (dir != null)
        {
            try
            {
                usable = File.Exists(Path.Combine(dir, id + ".atlas"))
                      && File.Exists(Path.Combine(dir, id + ".skel"));
            }
            catch (Exception ex) { Logger.Error($"[Skin] 检查 {id} 资源失败", ex); }
        }

        return new SkinEntry
        {
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
            Dir = dir,
            IsUsable = usable,
        };
    }

    /// <summary>
    /// 显示名三级兜底：配置名 → display.txt 首行 → 目录名。
    /// 读不到、内容为空、抛异常，一律继续往下退 —— 显示名必须永远有值。
    /// </summary>
    private static string ResolveName(string id, string dir)
    {
        // ① 配置名（用户手动指定）
        if (_configNames.TryGetValue(id, out string? configured) && !string.IsNullOrWhiteSpace(configured))
        {
            // 界面上是卡片标题，太长会撑破排版，硬截到 24 字符
            string n = configured.Trim();
            return n.Length > 24 ? n[..24] : n;
        }

        // ② display.txt 首行
        try
        {
            string txt = Path.Combine(dir, "display.txt");
            if (File.Exists(txt))
            {
                foreach (string line in File.ReadLines(txt))
                {
                    string n = line.Trim().TrimStart('\uFEFF');
                    if (n.Length > 0) return n.Length > 24 ? n[..24] : n;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Skin] 读取 {id}/display.txt 失败", ex);
        }

        // ③ 目录名
        return id;
    }
}
