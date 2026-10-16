using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

/// <summary>识别结果：候选缺陷 + 量化参数。</summary>
public class RepairRecognition
{
    public List<DefectIndexEntry> Candidates { get; init; } = [];
    public Dictionary<string, double> Quant { get; init; } = [];
    public List<string> MissingFields { get; init; } = [];
}


/// <summary>
/// 本地规则引擎：识别/判定/选型/核算全部确定性计算，LLM 仅负责后续润色成文。
/// </summary>
/// <summary>规则引擎组装的确定性维修计划。</summary>
public class RepairPlan
{
    public DefectIndexEntry Defect { get; init; } = new();
    public string Grade { get; init; } = "";
    public RepairScheme Scheme { get; init; } = new();
    public Dictionary<string, double> Quant { get; init; } = [];
    public List<string> MaterialLines { get; init; } = [];
    public string DraftHtml { get; set; } = "";
}
public class RepairRuleEngine 
{
    private readonly RepairKnowledgeService _kb;

    public RepairRuleEngine(RepairKnowledgeService kb) => _kb = kb;

    // ─────────────── ① 识别 ───────────────

    public RepairRecognition Recognize(string description)
    {
        description = (description ?? "").Trim();
        var quant = ExtractQuant(description);
        var grams = BuildGrams(description);

        var candidates = _kb.Kb.DefectIndex
            .Select(e => (Entry: e, Score: ScoreEntry(e, grams)))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(6)
            .Select(c => c.Entry)
            .ToList();

        var result = new RepairRecognition { Candidates = candidates, Quant = quant };
        if (quant.Count == 0)
            result.MissingFields.Add("量化参数（涉及层数/损伤长度mm/面积㎡）——建议在参数框补充");
        return result;
    }

    private static int ScoreEntry(DefectIndexEntry e, List<string> grams)
    {
        int score = 0;
        foreach (var g in grams)
        {
            if (g.Length < 2) continue;
            if (e.DefectType.Contains(g, StringComparison.OrdinalIgnoreCase)) score += 4;
            else if (e.Position.Contains(g, StringComparison.OrdinalIgnoreCase)) score += 3;
            else if (e.Definition.Contains(g, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }

    private static List<string> BuildGrams(string text)
    {
        var grams = new List<string>();
        foreach (Match m in Regex.Matches(text, @"[\d.]+\s*(?:层|mm|厘米|米|㎡|m²|平方米)"))
            grams.Add(m.Value.Replace(" ", ""));
        var clean = Regex.Replace(text, @"[，。；：、！？\s\d.（）()\-—/\\,.;:!?]", "");
        for (var i = 0; i + 2 <= clean.Length; i++)
            grams.Add(clean.Substring(i, 2));
        return grams.Distinct().ToList();
    }

    private static Dictionary<string, double> ExtractQuant(string text)
    {
        var quant = new Dictionary<string, double>();
        var ply = Regex.Match(text, @"(?:涉及层数|层数|布层)\s*[=:]?\s*(\d+)");
        if (ply.Success) quant["层数"] = double.Parse(ply.Groups[1].Value);
        var len = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*mm");
        if (len.Success) quant["长度mm"] = double.Parse(len.Groups[1].Value);
        var area = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*(?:㎡|m²|平方米)");
        if (area.Success) quant["面积"] = double.Parse(area.Groups[1].Value);
        return quant;
    }

    // ─────────────── ② 判定 ───────────────

    public (string? Grade, string Note) DetermineGrade(DefectIndexEntry defect, Dictionary<string, double> quant)
    {
        var rule = defect.GradeRule.Trim();

        // 拆解 "C：涉及层数≤6；B：涉及层数＞6" 形式的条件规则
        var evaluated = new List<(string Grade, string Cond)>();
        foreach (var part in Regex.Split(rule, @"[；;]"))
        {
            var m = Regex.Match(part.Trim(), @"^([A-E])\s*[:：]?\s*(.*)$");
            if (m.Success) evaluated.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
        }

        // 单一无条件等级（如 "C（Ⅰ区）"）
        if (evaluated.Count == 1 &&
            (evaluated[0].Cond.Length == 0 || evaluated[0].Cond.StartsWith("（") || evaluated[0].Cond.StartsWith("(")))
            return (evaluated[0].Grade, "按知识库定级规则直接判定");

        // 条件型：用量化参数求值（支持 层数/长度/面积 与比较符）
        foreach (var (grade, cond) in evaluated)
        {
            if (cond.Length == 0) continue;
            var cm = Regex.Match(cond, @"(涉及层数|层数|长度|面积)\s*([>＞≥<=＜≤]+)\s*(\d+(?:\.\d+)?)");
            if (!cm.Success) continue;
            var field = cm.Groups[1].Value.Contains("长度") ? "长度mm" : cm.Groups[1].Value;
            if (!quant.TryGetValue(field, out var value)) continue;

            var op = cm.Groups[2].Value;
            var threshold = double.Parse(cm.Groups[3].Value);
            var hit = op switch
            {
                ">=" or "≥" => value >= threshold,
                ">" or "＞" => value > threshold,
                "<=" or "≤" => value <= threshold,
                "<" or "＜" => value < threshold,
                _ => false,
            };
            if (hit) return (grade, $"按定级规则「{cond}」与量化参数判定");
        }

        // 条件无法求值：取规则中第一个等级并提示人工复核
        var first = evaluated.FirstOrDefault().Grade;
        if (string.IsNullOrEmpty(first))
            first = Regex.Match(rule, "[A-E]").Value;
        return (string.IsNullOrEmpty(first) ? null : first,
            "定级规则含条件但量化参数不足，暂按规则首档判定，请人工复核");
    }

    // ─────────────── ③ 选型 ───────────────

    public List<RepairScheme> SelectSchemes(DefectIndexEntry defect) =>
        defect.MatchedSchemes
            .Select(code => _kb.Kb.RepairSchemes.FirstOrDefault(s => s.Code == code))
            .Where(s => s != null).Cast<RepairScheme>().ToList();
}
