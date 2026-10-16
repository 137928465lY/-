using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

/// <summary>草稿组装 v2：标准章节树全展开（1.1~1.8 / 2.1~2.4 / 3.1~3.4 / 4.x / 5 / 6），数据表化。</summary>
public static class RepairPlanComposer
{
    private static readonly Dictionary<string, (string Name, string Disposal)> GradeInfo = new()
    {
        ["A"] = ("特别严重（Ⅰ级）", "风机禁止运行，立即停机修复或更换；出具 A 级缺陷报废单，结构设计部确认、总工批准。"),
        ["B"] = ("严重（Ⅱ级）", "建议停机维修；技术工程师出具维修方案，结构设计部确认等级，总工/副总工批准。"),
        ["C"] = ("较重（Ⅲ级）", "建议停机维修，可短暂运行；技术工程师组织确认等级并出具方案，售后部长批准。"),
        ["D"] = ("一般（Ⅳ级）", "短期内可正常运行，售后到达后尽快维修；区域经理审核并出具方案。"),
        ["E"] = ("轻微（Ⅴ级）", "正常运行，待巡检或时间合适时维修；按 E 级标准作业指导书处理。"),
    };

    public static RepairPlan ComposePlan(this RepairRuleEngine engine,
        DefectIndexEntry defect, string grade, RepairScheme scheme,
        Dictionary<string, double> quant, string defectDescription, string version)
    {
        quant.TryGetValue("层数", out var n);
        quant.TryGetValue("面积", out var area);
        quant.TryGetValue("长度mm", out var len);

        var materialLines = new List<string>();
        double fabricArea = 0;
        if (area > 0 && n > 0)
        {
            fabricArea = Math.Round(area * n * 1.30, 2);
            materialLines.Add("玻纤布（按原结构双轴/三轴/单轴）| ㎡ | " + fabricArea + "（= 损伤面积 " + area + " × 层数 " + n + " × 1.30，含 30% 错层余量）");
            materialLines.Add("手糊/灌注树脂（主剂+固化剂）| kg | 按浸润量现场核定（配比 100:30 量级），单次混配不超过 3kg、单人不超过 500g");
            materialLines.Add("脱模布 | ㎡ | 铺层周边外扩 50mm 计");
        }
        else
        {
            materialLines.Add("玻纤布（按原结构）| ㎡ | 现场按推层图实测下料（N x 20% 增强原则，最外层使用双轴布）");
            materialLines.Add("手糊/灌注树脂（主剂+固化剂）| kg | 按浸润量现场核定（配比 100:30 量级）");
        }
        foreach (var m in scheme.Materials)
            if (!materialLines.Any(l => l.Split('|')[0].Trim() == m))
                materialLines.Add(m + " | - | 按方案配套");

        var plan = new RepairPlan
        {
            Defect = defect,
            Grade = string.IsNullOrEmpty(grade) ? "C" : grade,
            Scheme = scheme,
            Quant = quant,
            MaterialLines = materialLines,
        };
        plan.DraftHtml = BuildDraftHtml(plan, defectDescription, version, n, area, len, fabricArea);
        return plan;
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string Tbl(IEnumerable<(string a, string b)> rows) =>
        "<table border=\"1\">" + string.Join("", rows.Select(r =>
            "<tr><td style=\"width:170px\">" + Esc(r.a) + "</td><td>" + Esc(r.b) + "</td></tr>")) + "</table>";

    private static string BuildDraftHtml(RepairPlan p, string desc, string version,
        double n, double area, double len, double fabricArea)
    {
        var d = p.Defect; var s = p.Scheme;
        if (!GradeInfo.TryGetValue(p.Grade, out var gradeInfo))
            gradeInfo = ("详见知识库", "按知识库处置权限执行。");
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>");

        sb.Append("<h1>1 叶片损伤情况分析</h1>");
        sb.Append("<h2>1.1 缺陷描述（现场输入）</h2>");
        var rows11 = new List<(string, string)> { ("原始描述", desc) };
        if (n > 0) rows11.Add(("涉及层数", n + " 层"));
        if (len > 0) rows11.Add(("损伤长度", len + " mm"));
        if (area > 0) rows11.Add(("损伤面积", area + " 平方米"));
        sb.Append(Tbl(rows11));
        sb.Append("<p>录入模板参考：" + Esc(d.InputTemplate) + "</p>");

        sb.Append("<h2>1.2 缺陷识别结果</h2>");
        sb.Append(Tbl(new List<(string, string)>
        {
            ("缺陷名称", d.DefectType),
            ("索引编号", d.Code),
            ("所在区域", d.Region),
            ("具体位置", d.Position),
            ("缺陷等级", p.Grade + " 级"),
            ("缺陷定义", d.Definition),
        }));

        sb.Append("<h2>1.3 缺陷位置与形态</h2>");
        sb.Append("<p>该缺陷位于「" + Esc(d.Region) + "」区域的「" + Esc(d.Position) + "」。" + Esc(d.Definition) + "</p>");
        sb.Append("<p>现场计量方式：测面积（平方米）、展向长度与弦向宽度（mm）、涉及层数 N，必要时敲击定界。</p>");
        var parts = new List<string>();
        if (len > 0) parts.Add("长度约 " + len + "mm");
        if (area > 0) parts.Add("面积约 " + area + " 平方米");
        if (n > 0) parts.Add("涉及层数 " + n + " 层");
        sb.Append(parts.Count > 0
            ? "<p>尺寸判定：" + Esc(string.Join("，", parts)) + "。</p>"
            : "<p>尺寸判定：现场实测后补充（涉及层数 N、长度、面积）。</p>");

        sb.Append("<h2>1.4 缺陷等级与处置意见</h2>");
        var q14 = p.Quant.Count > 0 ? "；实测参数：" + string.Join("，", p.Quant.Select(kv => kv.Key + "=" + kv.Value)) : "";
        sb.Append(Tbl(new List<(string, string)>
        {
            ("缺陷等级", p.Grade + " 级（" + gradeInfo.Name + "）"),
            ("处置意见", gradeInfo.Disposal),
            ("风机运行建议", d.OperationAdvice),
            ("定级依据", d.GradeRule + q14),
        }));

        sb.Append("<h2>1.5 缺陷标准化描述</h2>");
        sb.Append("<p>" + Esc(d.InputTemplate) + "</p>");
        sb.Append("<p>上述「__」占位项为须现场实测补充的量化指标。</p>");

        sb.Append("<h2>1.6 判定依据（位置-结构核对）</h2>");
        sb.Append("<p>依据《厂外叶片损伤分级控制标准（A2/A3 版）》分级容许阈值表与知识库缺陷索引交叉核对：缺陷编码 " +
                  Esc(d.Code) + "，定级规则「" + Esc(d.GradeRule) + "」，来源条款 " + Esc(d.SourceRef) + "。</p>");
        sb.Append("<p>核心原则：叶片损伤严重程度排序为 重要结构性损伤 > 一般结构性损伤 > 功能性损伤；" +
                  "重要结构性部件（主梁/叶根/后缘UD/前缘粘接角）阈值更严，功能性部件阈值更宽。</p>");
        sb.Append("<h1>2 维修方案</h1>");
        sb.Append("<h2>2.1 维修范围</h2>");
        sb.Append("<p>本方案适用于「" + Esc(d.Code) + " " + Esc(d.DefectType) + "」（" + Esc(d.Position) + "）的修复，采用 " + Esc(s.Code) + " " + Esc(s.Name) + "（" + Esc(s.Category) + "，" + Esc(s.Process) + "）。修复范围以现场打磨确认的损伤边界为准，边界外扩至完好层压板区域（打磨斜面过渡），最终范围由技术工程师现场确认。</p>");

        sb.Append("<h2>2.2 维修步骤</h2><p>按以下工步逐项执行：</p><ol>");
        foreach (var step in s.Steps) sb.Append("<li>" + Esc(step) + "</li>");
        sb.Append("</ol>");
        sb.Append("<p>固化与周期：" + Esc(s.Cycle) + "</p>");

        sb.Append("<h2>2.3 维修周期与人员配置</h2>");
        sb.Append(Tbl(new List<(string, string)>
        {
            ("班组配置", "按厂外叶片维修标准班组配置（外部作业 4 人：高空 1~2 人 + 地面配合 2~3 人）；内部作业 3 人。关键工序由技术工程师现场确认。"),
            ("单处维修周期", s.Cycle),
            ("工期说明", "固化时间按环境温度调整（60Cx5h / 65Cx4h / 70Cx5h 区间，以现场维修方案确认值为准）；表干后可打磨时间按知识库要求执行。"),
        }));

        sb.Append("<h2>2.4 相似维修案例参考</h2>");
        sb.Append("<p>【相似案例清单由知识库检索结果填充，LLM 润色时可引用其工艺要点，但不得改动本方案数值。】</p>");

        sb.Append("<h1>3 物料和工具</h1>");
        sb.Append("<h2>3.1 维修材料</h2><table border=\"1\"><tr><th>名称</th><th>单位</th><th>用量/说明</th></tr>");
        foreach (var line in p.MaterialLines)
        {
            var seg = line.Split('|');
            if (seg.Length >= 3)
                sb.Append("<tr><td>" + Esc(seg[0].Trim()) + "</td><td>" + Esc(seg[1].Trim()) + "</td><td>" + Esc(seg[2].Trim()) + "</td></tr>");
            else
                sb.Append("<tr><td colspan=\"3\">" + Esc(line) + "</td></tr>");
        }
        sb.Append("</table>");
        sb.Append("<h2>3.2 维修辅材</h2>");
        sb.Append("<p>脱模布、隔离膜、真空袋膜、导流网、密封胶条、螺旋管、注胶嘴等辅材按真空灌注/手糊工艺配套领用，用量以现场实耗为准。</p>");
        sb.Append("<h2>3.3 设备工具</h2><table border=\"1\"><tr><th>序号</th><th>工具</th></tr>");
        var ti = 1;
        foreach (var t in s.Tools) sb.Append("<tr><td>" + (ti++) + "</td><td>" + Esc(t) + "</td></tr>");
        sb.Append("</table>");
        sb.Append("<h2>3.4 劳保用品</h2>");
        sb.Append("<p>安全帽、全身式安全带/双钩、防尘口罩、防护眼镜、丁腈手套、防静电工作服、安全绳等，按现场作业规程配置。</p>");

        sb.Append("<h1>4 验收与作业条件</h1>");
        sb.Append("<h2>4.1 质量验收标准</h2><ul>");
        foreach (var a in s.Acceptance) sb.Append("<li>" + Esc(a) + "</li>");
        sb.Append("</ul>");
        sb.Append("<h2>4.2 作业环境条件</h2>");
        sb.Append("<p>温度：正常作业窗口 10C-T35C；5-10C 或 35-40C 须增加专项质量管控（加热固化方案）；-5C-T10C 或 T40C 以上现场停止作业，须技术工程师指导下评估。</p>");
        sb.Append("<p>湿度：正常作业窗口 20%-75%RH；75%-80%RH 须专项除湿方案；RH 80% 以上禁止维修类作业。</p>");
        sb.Append("<h2>4.3 方案注意事项</h2><ul>");
        foreach (var note in s.Notes) sb.Append("<li>" + Esc(note) + "</li>");
        sb.Append("</ul>");

        sb.Append("<h1>5 风险管控</h1>");
        sb.Append("<p>树脂混配严格按厂家比例，胶温超过 38 摄氏度禁用，超期胶液禁止使用；加热固化全程监控温度曲线，防止局部过热；高空作业执行登高规程，内部作业执行受限空间规程，停机锁定（LOTO）落实后方可作业；打磨粉尘佩戴防尘口罩并现场回收；关键指标（巴氏硬度、绝缘电阻、固化度）检测记录随工单归档；危废按环保要求分类处置。</p>");

        sb.Append("<h1>6 修订记录</h1>");
        sb.Append("<table border=\"1\"><tr><th>版本</th><th>日期</th><th>修订内容</th><th>编制</th></tr>");
        sb.Append("<tr><td>" + Esc(version) + "</td><td>" + DateTime.Now.ToString("yyyy-MM-dd") + "</td><td>首次发布（知识库 " + Esc(s.Code) + " 规则引擎生成）</td><td></td></tr></table>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}