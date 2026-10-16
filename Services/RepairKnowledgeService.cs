using System;
using System.IO;
using System.Text.Json;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;


/// <summary>
/// 维修知识库加载服务：读取 SeedKB/叶片维修知识库.json（结构化方案库，46 方案/107 缺陷/112 分级条目）。
/// </summary>
public class RepairKnowledgeService 
{
    public RepairKbRoot Kb { get; } = new();
    public bool Loaded { get; private set; }

    public RepairKnowledgeService()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "SeedKB", "叶片维修知识库.json");
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<RepairKbRoot>(json);
            if (root == null) return;

            Kb.DefectIndex = root.DefectIndex;
            Kb.GradeLookupTable = root.GradeLookupTable;
            Kb.RepairSchemes = root.RepairSchemes;
            Kb.AcceptanceStandards = root.AcceptanceStandards;
            Kb.EnvironmentWindows = root.EnvironmentWindows;
            Kb.MaterialRatios = root.MaterialRatios;
            Kb.ProcessSelection = root.ProcessSelection;
            Loaded = true;
        }
        catch
        {
            Loaded = false;
        }
    }
}
