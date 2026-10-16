using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 叶片缺陷审核.Models;

/// <summary>缺陷索引条目（叶片维修知识库.json / defect_index，107 条）。</summary>
public class DefectIndexEntry
{
    [JsonPropertyName("缺陷编码")] public string Code { get; set; } = "";
    [JsonPropertyName("区域")] public string Region { get; set; } = "";
    [JsonPropertyName("位置")] public string Position { get; set; } = "";
    [JsonPropertyName("缺陷类型")] public string DefectType { get; set; } = "";
    [JsonPropertyName("定级规则")] public string GradeRule { get; set; } = "";
    [JsonPropertyName("风机运行建议")] public string OperationAdvice { get; set; } = "";
    [JsonPropertyName("录入描述模板")] public string InputTemplate { get; set; } = "";
    [JsonPropertyName("匹配维修方案")] public List<string> MatchedSchemes { get; set; } = [];
    [JsonPropertyName("来源条款")] public string SourceRef { get; set; } = "";
    [JsonPropertyName("定义")] public string Definition { get; set; } = "";
}

/// <summary>分级容许阈值表条目（grade_lookup_table，112 条）。</summary>
public class GradeLookupEntry
{
    [JsonPropertyName("一级区域")] public string Region1 { get; set; } = "";
    [JsonPropertyName("二级区域")] public string Region2 { get; set; } = "";
    [JsonPropertyName("三级区域")] public string Region3 { get; set; } = "";
    [JsonPropertyName("故障类型")] public string FaultType { get; set; } = "";
    [JsonPropertyName("判断标准")] public string Criteria { get; set; } = "";
    [JsonPropertyName("故障等级")] public string Grade { get; set; } = "";
    [JsonPropertyName("风机运行建议")] public string OperationAdvice { get; set; } = "";
}

/// <summary>维修方案（repair_schemes，46 条，16 键结构化）。</summary>
public class RepairScheme
{
    [JsonPropertyName("方案编号")] public string Code { get; set; } = "";
    [JsonPropertyName("标准名称")] public string Name { get; set; } = "";
    [JsonPropertyName("分类")] public string Category { get; set; } = "";
    [JsonPropertyName("成型工艺")] public string Process { get; set; } = "";
    [JsonPropertyName("适用缺陷")] public List<string> ApplicableDefects { get; set; } = [];
    [JsonPropertyName("基本流程")] public List<string> Steps { get; set; } = [];
    [JsonPropertyName("工器具")] public List<string> Tools { get; set; } = [];
    [JsonPropertyName("材料")] public List<string> Materials { get; set; } = [];
    [JsonPropertyName("验收标准")] public List<string> Acceptance { get; set; } = [];
    [JsonPropertyName("周期")] public string Cycle { get; set; } = "";
    [JsonPropertyName("参考图")] public List<string> Figures { get; set; } = [];
    [JsonPropertyName("备注")] public List<string> Notes { get; set; } = [];
}

/// <summary>验收标准条目（acceptance_standards，44 条）。</summary>
public class AcceptanceStandard
{
    [JsonPropertyName("类别")] public string Category { get; set; } = "";
    [JsonPropertyName("项目")] public string Item { get; set; } = "";
    [JsonPropertyName("标准/判据")] public string Criteria { get; set; } = "";
    [JsonPropertyName("检测方法")] public string Method { get; set; } = "";
    [JsonPropertyName("来源")] public string Source { get; set; } = "";
}

/// <summary>工艺选择规则（process_selection）。</summary>
public class ProcessSelectionRule
{
    [JsonPropertyName("情形")] public string Situation { get; set; } = "";
    [JsonPropertyName("层数N")] public string PlyCondition { get; set; } = "";
    [JsonPropertyName("工艺")] public string Process { get; set; } = "";
}

/// <summary>维修知识库根对象（叶片维修知识库.json）。</summary>
public class RepairKbRoot
{
    [JsonPropertyName("meta")] public JsonElement Meta { get; set; }
    [JsonPropertyName("grading")] public JsonElement Grading { get; set; }
    [JsonPropertyName("coding_rules")] public JsonElement CodingRules { get; set; }
    [JsonPropertyName("defect_index")] public List<DefectIndexEntry> DefectIndex { get; set; } = [];
    [JsonPropertyName("grade_lookup_table")] public List<GradeLookupEntry> GradeLookupTable { get; set; } = [];
    [JsonPropertyName("repair_schemes")] public List<RepairScheme> RepairSchemes { get; set; } = [];
    [JsonPropertyName("acceptance_standards")] public List<AcceptanceStandard> AcceptanceStandards { get; set; } = [];
    [JsonPropertyName("environment_windows")] public JsonElement EnvironmentWindows { get; set; }
    [JsonPropertyName("material_ratios")] public JsonElement MaterialRatios { get; set; }
    [JsonPropertyName("process_selection")] public List<ProcessSelectionRule> ProcessSelection { get; set; } = [];
    [JsonPropertyName("tools_catalog")] public JsonElement ToolsCatalog { get; set; }
    [JsonPropertyName("materials_catalog")] public JsonElement MaterialsCatalog { get; set; }
    [JsonPropertyName("figures")] public JsonElement Figures { get; set; }
    [JsonPropertyName("data_dictionary")] public JsonElement DataDictionary { get; set; }
}
