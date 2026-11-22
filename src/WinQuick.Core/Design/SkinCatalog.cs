using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WinQuick.Core.Design;

/// <summary>
/// 一套皮肤：一组**局部**的令牌覆盖（材质 + 动效）。
/// </summary>
public sealed record SkinSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tagline")] public string Tagline { get; init; } = "";
    [JsonPropertyName("cost")] public string Cost { get; init; } = "";
    [JsonPropertyName("src")] public string Src { get; init; } = "";

    /// <summary>覆盖的令牌，形如 <c>{ "color": {...}, "motion": {...} }</c>。</summary>
    [JsonPropertyName("overrides")] public JsonObject? Overrides { get; init; }
}

/// <summary>
/// 皮肤目录。
///
/// ## 为什么皮肤是「合并到基线上的一层覆盖」而不是「一整套完整令牌」
///
/// 一整套完整令牌意味着**第二份真源**：tokens.json 改了基线，每套皮肤里那份副本不会跟着变，
/// 于是"改了设计基线但界面上只有默认皮肤跟着变"这种分叉要到用户看着不对劲才会被发现。
/// 覆盖则不可能漂移 —— 没被覆盖的每个字段都只有一处定义。
///
/// ## 合并之后仍然跑同一套 Validate()
///
/// 这是关键：皮肤**不能**绕开被单测锁住的不变量（死区比例、子环给悬停鼓出留余量、
/// 标签半宽下限…）。所以合并出来的结果与基线一样要被校验，写错一处就会当场抛异常，
/// 而不是等用户看到一圈挤在一起的文字。
///
/// ## 哪些字段不给皮肤碰
///
/// · <c>interaction</c> 整节不给 —— 悬停延迟、长按阈值是**行为**而不是外观，
///   改它们会让"同一个动作在不同皮肤下手感不同"，那是 bug。
/// · <c>radius.windowSize</c> 永远忽略 —— 改窗口尺寸会让分层窗口重建整块表面，
///   那正是本项目报过三次闪烁的那类操作。皮肤不许动窗口大小。
/// </summary>
public sealed class SkinCatalog
{
    /// <summary>允许皮肤覆盖的节。</summary>
    private static readonly string[] AllowedSections = ["color", "radius", "typography", "motion"];

    /// <summary>默认皮肤 id。找不到指定皮肤时也回退到它（基线本身）。</summary>
    public const string DefaultId = "default";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly List<SkinSpec> _skins;

    private SkinCatalog(List<SkinSpec> skins) => _skins = skins;

    public IReadOnlyList<SkinSpec> All => _skins;

    public SkinSpec? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : _skins.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>列表里有这个 id 吗（用来区分"用户选了默认"与"用户选了一个不存在的皮肤"）。</summary>
    public bool Contains(string? id) => Find(id) is not null;

    public static SkinCatalog Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<CatalogDto>(json, Options)
            ?? throw new InvalidOperationException("skins.json 解析结果为空");

        if (dto.Skins.Count == 0)
            throw new InvalidOperationException("skins.json 里一套皮肤都没有");

        var duplicate = dto.Skins.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"skins.json 里 id “{duplicate.Key}” 重复了");

        return new SkinCatalog(dto.Skins);
    }

    /// <summary>
    /// 把某套皮肤合并到基线令牌上，返回合并后的 JSON（空/未知皮肤返回基线原文）。
    /// </summary>

    public string Merge(string baseTokensJson, string? skinId)
    {
        var spec = Find(skinId);
        if (spec?.Overrides is null)
        {
            // 「没有皮肤」与「default 皮肤（空覆盖）」必须是同一套值 —— 基线路径同样回填次要面派生键，
            // 否则这两者之间一切换，死区颜色就差一步（回填语义见 SecondaryColors）。
            var baseRoot = JsonNode.Parse(baseTokensJson) as JsonObject
                ?? throw new InvalidOperationException("tokens.json 解析结果不是 JSON 对象");
            SecondaryColors.FillMissing(baseRoot["color"] as JsonObject, new HashSet<string>());
            return baseRoot.ToJsonString();
        }

        return MergeWithOverrides(baseTokensJson, spec.Overrides);
    }

    /// <summary>
    /// 把指定 overrides 合并到基线 JSON —— Merge 的后半段（接受 JsonObject），
    /// 供 App 侧运行时合成的皮肤（「跟随系统」，spec §5）复用，与静态皮肤同一条路径。
    /// </summary>

    public string MergeWithOverrides(string baseTokensJson, JsonObject overrides)
    {
        var root = JsonNode.Parse(baseTokensJson) as JsonObject
            ?? throw new InvalidOperationException("tokens.json 解析结果不是 JSON 对象");

        ApplyOverrides(root, overrides);

        // 次要面统一派生（spec skin-color-redesign R3）：皮肤没显式覆盖的死区/角标键回填派生值。
        // 放在这里而不是渲染层：静态皮肤 Merge 与「跟随系统」Compose 都汇到这条路径，回填一次覆盖所有皮肤。
        var covered = overrides["color"] is JsonObject colorKeys
            ? (IReadOnlySet<string>)colorKeys.Select(kv => kv.Key.ToLowerInvariant()).ToHashSet()
            : new HashSet<string>();
        SecondaryColors.FillMissing(root["color"] as JsonObject, covered);

        return root.ToJsonString();
    }

    private static void ApplyOverrides(JsonObject target, JsonObject overrides)
    {
        foreach (var (sectionName, sectionValue) in overrides)
        {
            // JSON 里用 "_" 开头的字段写说明，不参与合并。
            if (sectionName.StartsWith('_')) continue;
            if (!AllowedSections.Contains(sectionName, StringComparer.OrdinalIgnoreCase)) continue;
            if (sectionValue is not JsonObject section) continue;
            if (target[sectionName] is not JsonObject baseSection) continue;

            foreach (var (key, value) in section)
            {
                if (key.StartsWith('_')) continue;

                // 窗口尺寸是硬禁区：见类型注释。
                if (sectionName.Equals("radius", StringComparison.OrdinalIgnoreCase) &&
                    key.Equals("windowSize", StringComparison.OrdinalIgnoreCase))
                    continue;

                baseSection[key] = value?.DeepClone();
            }
        }
    }

    private sealed record CatalogDto
    {
        [JsonPropertyName("skins")] public List<SkinSpec> Skins { get; init; } = [];
    }
}
