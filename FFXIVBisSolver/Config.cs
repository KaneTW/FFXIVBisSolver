using System;
using System.Collections.Generic;
using SaintCoinach.Xiv;

namespace FFXIVBisSolver
{
    //TODO: not-nullable
    public class JobConfig
    {
        public Dictionary<BaseParam, double> Weights { get; set; }
        public Dictionary<BaseParam, int> StatRequirements { get; set; }
    }

    public class SolverConfig
    {
        public Dictionary<ClassJob, JobConfig> JobConfigs { get; set; }
        public Dictionary<int, RelicConfig> RelicConfigs { get; set; }
        public Dictionary<BaseParam, int> BaseStats { get; set; }
        public List<int> RequiredItems { get; set; }
        public int OvermeldThreshold { get; set; }
        public int AllocatedStatsCap { get; set; }
        public bool MaximizeUnweightedValues { get; set; }
        public bool SolverSupportsSOS { get; set; }
    }

    //TODO: this can be handled better
    public class RelicConfig
    {
        public List<int> Items { get; set; }
        public int StatCap { get; set; }
        public List<List<int>> ConversionMap{ get; set; }
        public Dictionary<BaseParam, List<List<int>>> ConversionOverride { get; set; }
    }
}