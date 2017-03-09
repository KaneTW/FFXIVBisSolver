using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using MoreLinq;
using OPTANO.Modeling.Optimization;
using OPTANO.Modeling.Optimization.Enums;
using OPTANO.Modeling.Optimization.Interfaces;
using OPTANO.Modeling.Optimization.Solver;
using SaintCoinach.Text.Nodes;
using SaintCoinach.Xiv;
using SaintCoinach.Xiv.Items;

namespace FFXIVBisSolver
{
    public class BisModel
    {
        public const int MaxMateriaSlots = 5;

        public readonly string[] MainStats =
        {
            "Vitality",
            "Strength",
            "Dexterity",
            "Vitality",
            "Intelligence",
            "Mind",
            "Piety"
        };

        public readonly string[] TieredStats = {"Skill Speed", "Spell Speed"};

        /// <summary>
        ///     Creates a new BiS solver model.
        /// </summary>
        /// <param name="solverConfig">Solver configuration</param>
        /// <param name="job">Job to solve for</param>
        /// <param name="gearChoices">Gear items to choose from. Keep this small.</param>
        /// <param name="foodChoices">List of food choices. This can be fairly large, it doesn't add much complexity.</param>
        /// <param name="materiaChoices">
        ///     Dictionary of materia choices; set value to true if the materia is allowed for advanced
        ///     melding. The materia with the highest eligible stat value is chosen. (Note that Tier is 0-indexed)
        /// </param>
        //TODO: this is getting out of hand. need config object asap.
        //TODO: make model parts pluggable if possible
        public BisModel(SolverConfig solverConfig, ClassJob job,
            IEnumerable<Equipment> gearChoices, IEnumerable<FoodItem> foodChoices,
            IDictionary<MateriaItem, bool> materiaChoices)
        {
            Model = new Model();

            SolverConfig = solverConfig;
            Job = job;
            JobConfig = SolverConfig.JobConfigs[Job];

            GearChoices = gearChoices.ToList();
            FoodChoices = foodChoices.ToList();
            
            // collect stats we care about
            RelevantStats = JobConfig.Weights.Keys.Union(JobConfig.StatRequirements.Keys).ToList();


            // we don't care about materia which affect unneeded stats
            MateriaChoices = materiaChoices.Where(m => RelevantStats.Contains(m.Key.BaseParam))
                .ToDictionary(k => k.Key, k => k.Value);

            var allEquipSlots = GearChoices.SelectMany(g => g.EquipSlotCategory.PossibleSlots).ToList();

            gear = new VariableCollection<EquipSlot, Equipment>(Model, allEquipSlots, GearChoices,
                type: VariableType.Binary,
                debugNameGenerator: (s, e) => new StringBuilder().AppendFormat("{0}_{1}", s, e),
                lowerBoundGenerator: (s, e) => CheckRequired(e) ? 1 : 0);
            food = new VariableCollection<FoodItem>(Model, FoodChoices, 
                type: VariableType.Binary, 
                debugNameGenerator: e => new StringBuilder().AppendFormat("{0}", e.Item),
                lowerBoundGenerator: i => CheckRequired(i.Item) ? 1 : 0);
            foodcap = new VariableCollection<FoodItem, BaseParam>(Model, FoodChoices, RelevantStats,
                type: VariableType.Integer,
                debugNameGenerator: (i, bp) => new StringBuilder().AppendFormat("{0}_{1}_cap", i.Item, bp),
                lowerBoundGenerator: (x, b) => 0);
            materia = new VariableCollection<EquipSlot, Equipment, MateriaItem>(Model, allEquipSlots, GearChoices, MateriaChoices.Keys, 
                type: VariableType.Integer, 
                debugNameGenerator: (s, e, m) => new StringBuilder().AppendFormat("{2}_{0}_{1}", s, e, m),
                lowerBoundGenerator: (s, e, bp) => 0, 
                upperBoundGenerator:(s,e,b) => e.TotalMateriaSlots());
            cap = new VariableCollection<EquipSlot, Equipment, BaseParam>(Model, allEquipSlots, GearChoices, RelevantStats, 
                type: VariableType.Integer,
                debugNameGenerator: (s, e, bp) => new StringBuilder().AppendFormat("{2}_cap_{0}_{1}", s,e,bp),
                lowerBoundGenerator: (s, e, b) => 0);
            relicBase = new VariableCollection<EquipSlot, Equipment, BaseParam>(Model, allEquipSlots, GearChoices, RelevantStats, 
                type: VariableType.Integer,
                debugNameGenerator: (s, e, bp) => new StringBuilder().AppendFormat("{2}_relic_base_{0}_{1}", s, e, bp),
                lowerBoundGenerator: (s, e, b) => 0);

            stat = new VariableCollection<BaseParam>(Model, RelevantStats, 
                type: VariableType.Integer,
                debugNameGenerator: bp => new StringBuilder().AppendFormat("gear_materia__{0}", bp),
                lowerBoundGenerator: x => 0);
            modstat = new VariableCollection<BaseParam>(Model, RelevantStats, 
                type: VariableType.Integer,
                debugNameGenerator: bp => new StringBuilder().AppendFormat("added_food_{0}", bp),
                lowerBoundGenerator: x => 0);
            allocstat = new VariableCollection<BaseParam>(Model, RelevantStats, 
                type: VariableType.Integer,
                debugNameGenerator: bp => new StringBuilder().AppendFormat("allocated_{0}", bp),
                lowerBoundGenerator: x => 0, upperBoundGenerator: x => SolverConfig.AllocatedStatsCap);
            tieredstat = new VariableCollection<BaseParam>(Model, RelevantStats, 
                type: VariableType.Integer,
                debugNameGenerator: bp => new StringBuilder().AppendFormat("tiered_{0}", bp),
                lowerBoundGenerator: x => 0);

            Model.AddConstraint(
                Expression.Sum(RelevantStats.Where(bp => MainStats.Contains(bp.Name)).Select(bp => allocstat[bp])) <=
                SolverConfig.AllocatedStatsCap,
                "cap allocatable stats");

            StatExprs = RelevantStats.ToDictionary(bp => bp, bp => Expression.EmptyExpression);
            FoodExprs = RelevantStats.ToDictionary(bp => bp, bp => (Expression) stat[bp]);
            SolverConfig.BaseStats.ForEach(kv => StatExprs[kv.Key] = kv.Value + Expression.EmptyExpression);

            CreateGearModel();

            CreateMateriaOrdering();
            
            CreateFoodModel();

            CreateTiers();

            CreateObjective();
        }

        private void CreateTiers()
        {
            RelevantStats.Where(bp => !CheckTiered(bp))
                .ForEach(bp => Model.AddConstraint(tieredstat[bp] == (Expression) modstat[bp]));

            foreach (var bp in RelevantStats.Where(CheckTiered))
            {
                //TODO: this can probably be optimized
                var max = CalculateUpperBound(bp);
                var pw = XivUtils.CreateSSTiers(JobConfig.BaseCastTimes, JobConfig.CastTimeBuffs, SolverConfig.BaseStats[bp], max);
                pw.AddToModel(Model, modstat[bp], tieredstat[bp], SolverConfig.SolverSupportsSOS);
            }
        }

        private int CalculateUpperBound(BaseParam bp)
        {
            return 13 * GearChoices.Max(e => e.GetMaximumParamValue(bp));
        }

        private bool CheckRequired(Item i)
        {
            if (SolverConfig.RequiredItems != null && SolverConfig.RequiredItems.Contains(i.Key))
            {
                SolverConfig.RequiredItems.Remove(i.Key);
                return true;
            }
            return false;
        }

        private bool CheckTiered(BaseParam bp)
        {
            return SolverConfig.UseTiers && TieredStats.Contains(bp.Name);
        }
        

        public Model Model { get; }
        public SolverConfig SolverConfig { get; set; }
        public ClassJob Job { get; }
        public JobConfig JobConfig { get; }
        public IList<Equipment> GearChoices { get; }
        public IList<FoodItem> FoodChoices { get; }
        public IDictionary<MateriaItem, bool> MateriaChoices { get; }
        public IList<BaseParam> RelevantStats { get; }


        //TODO: ok this is getting out of hand
        public VariableCollection<BaseParam> stat { get; }
        public VariableCollection<BaseParam> modstat { get; }
        public VariableCollection<BaseParam> allocstat { get; }
        public VariableCollection<BaseParam> tieredstat { get; }
        public VariableCollection<EquipSlot, Equipment> gear { get; }
        public VariableCollection<FoodItem> food { get; }
        public VariableCollection<FoodItem, BaseParam> foodcap { get; }
        public VariableCollection<EquipSlot, Equipment, MateriaItem> materia { get; }
        public VariableCollection<EquipSlot, Equipment, BaseParam> cap { get; }
        public VariableCollection<EquipSlot, Equipment, BaseParam> relicBase { get; }


        private Expression DummyObjective { get; set; } = Expression.EmptyExpression;
        private Dictionary<BaseParam, Expression> StatExprs { get; }
        private Dictionary<BaseParam, Expression> FoodExprs { get; }


        public IEnumerable<Equipment> ChosenGear
        {
            get { return gear.VarCollToDict().Where(kv => kv.Key.Value > 0).Select(kv => (Equipment) kv.Value[1]); }
        }

        public FoodItem ChosenFood
        {
            get { return (FoodItem) food.VarCollToDict().SingleOrDefault(kv => kv.Key.Value > 0).Value?[0]; }
        }

        public IEnumerable<Tuple<EquipSlot, Equipment, MateriaItem, int>> ChosenMateria
        {
            get
            {
                return
                    materia.VarCollToDict()
                        .Where(kv => kv.Key.Value > 0)
                        .Select(
                            kv =>
                                Tuple.Create((EquipSlot) kv.Value[0], (Equipment) kv.Value[1], (MateriaItem) kv.Value[2],
                                    Convert.ToInt32(kv.Key.Value)));
            }
        }

        public IEnumerable<Tuple<EquipSlot, BaseParam, int>> ChosenRelicStats => GetRelicStats(cap);

        public IEnumerable<Tuple<EquipSlot, BaseParam, int>> ChosenRelicDistribution => GetRelicStats(relicBase);

        private IEnumerable<Tuple<EquipSlot, BaseParam, int>> GetRelicStats(VariableCollection<EquipSlot,Equipment,BaseParam> vc)
        {
            return
                vc.VarCollToDict()
                    .Select(
                        kv =>
                            new
                            {
                                Variable = kv.Key,
                                EquipSlot = (EquipSlot) kv.Value[0],
                                Equipment = (Equipment) kv.Value[1],
                                BaseParam = (BaseParam) kv.Value[2]
                            })
                    .Where(
                        kv => kv.Variable.Value > 0 &&
                              ChosenGear.Contains(kv.Equipment) &&
                              SolverConfig.RelicConfigs.ContainsKey(kv.Equipment.ItemLevel.Key) &&
                              SolverConfig.RelicConfigs[kv.Equipment.ItemLevel.Key].Items.Contains(kv.Equipment.Key))
                    .Select(
                        kv =>
                            Tuple.Create(kv.EquipSlot, kv.BaseParam,
                                Convert.ToInt32(kv.Variable.Value)));
        }

        public Dictionary<BaseParam, int> ResultTotalStats => modstat.GetResultStat();
        public Dictionary<BaseParam, int> ResultAllocatableStats => allocstat.GetResultStat();
        public double ResultWeight { get; private set; }

        public bool IsSolved { get; private set; }

        private void CreateObjective()
        {
            var objExpr = Expression.EmptyExpression;
            foreach (var bp in RelevantStats)
            {
                if (MainStats.Contains(bp.Name))
                {
                    StatExprs.AddExprToDict(bp, allocstat[bp]);
                }

                Model.AddConstraint(stat[bp] == StatExprs[bp], "set collected stat " + bp);
                Model.AddConstraint(modstat[bp] <= FoodExprs[bp], "relative food bonuses for " + bp);

                if (JobConfig.Weights.ContainsKey(bp))
                {
                    if (SolverConfig.MaximizeUnweightedValues && TieredStats.Contains(bp.Name))
                    {
                        // add a small dummy weight so shown stats are amxed out
                        DummyObjective += modstat[bp]*1e-5;
                    }
                    objExpr += JobConfig.Weights[bp]*tieredstat[bp];
                }

                if (JobConfig.StatRequirements.ContainsKey(bp))
                {
                    if (SolverConfig.MaximizeUnweightedValues && !JobConfig.Weights.ContainsKey(bp))
                    {
                        DummyObjective += modstat[bp]*1e-5;
                    }
                    Model.AddConstraint(modstat[bp] >= JobConfig.StatRequirements[bp], "satisfy stat requirement for " + bp);
                }
            }
            Model.AddObjective(new Objective(objExpr + DummyObjective, "stat weight", ObjectiveSense.Maximize),
                "stat weight");
        }

        private void CreateFoodModel()
        {
            // avoid trivial constraints
            if (!FoodChoices.Any())
            {
                return;
            }

            Model.AddConstraint(Expression.Sum(FoodChoices.Select(x => food[x])) <= 1, "use one food only");

            foreach (var itm in FoodChoices)
            {
                var fd = itm.Food;
                var fv = food[itm];

                // SIMPLIFICATION: no one cares about nq food.
                foreach (var prm in fd.Parameters)
                {
                    var bp = prm.BaseParam;
                    if (!RelevantStats.Contains(bp))
                        continue;

                    var pvals = prm.Values.Where(v => v.Type == ParameterType.Hq).ToList();

                    // easy case, these just behave like normal gear
                    // ASSUMPTION: each food provides either a fixed or relative buff for a given base param
                    foreach (var pval in pvals.OfType<ParameterValueFixed>())
                    {
                        FoodExprs.AddExprToDict(bp, pval.Amount*fv);
                    }

                    foreach (var pval in pvals.OfType<ParameterValueRelative>())
                    {
                        // add relative modifier stat[bp]
                        Model.AddConstraint(foodcap[itm, bp] <= pval.Amount*stat[bp],
                            $"relative modifier for food {fd} in slot {bp}");

                        var limited = pval as ParameterValueRelativeLimited;
                        if (limited != null)
                        {
                            Model.AddConstraint(foodcap[itm, bp] <= limited.Maximum*fv,
                                $"cap for relative modifier for food {fd} in slot {bp}");
                        }

                        FoodExprs.AddExprToDict(bp, foodcap[itm, bp]);
                    }
                }
            }
        }

        private void CreateGearModel()
        {
            foreach (var grp in GearChoices.GroupBy(g => g.EquipSlotCategory))
            {
                // if gear is unique, equip it once only.
                if (grp.Key.PossibleSlots.Count() > 1)
                {
                    grp.Where(e => e.IsUnique)
                        .ForEach(
                            e =>
                                Model.AddConstraint(Expression.Sum(grp.Key.PossibleSlots.Select(s => gear[s, e])) <= 1,
                                    $"ensure gear uniqueness for {e}"));
                }

                // SIMPLIFICATION: we ignore blocked slots
                foreach (var s in grp.Key.PossibleSlots)
                {
                    // choose at most one gear per slot
                    Model.AddConstraint(Expression.Sum(grp.Select(e => gear[s, e])) <= 1,
                        $"choose at most one item for slot {s}");
                    foreach (var e in grp)
                    {
                        var gv = gear[s, e];

                        // ASSUMPTION: all gear choices have fixed parameters
                        var stats =
                            e.AllParameters.Where(p => RelevantStats.Contains(p.BaseParam))
                                .ToDictionary(p => p.BaseParam,
                                    p => p.Values.OfType<ParameterValueFixed>().Select(val => val.Amount).Sum());

                        if (SolverConfig.EquipmentOverrides?.ContainsKey(e) ?? false)
                        {
                            var statsOverride = SolverConfig.EquipmentOverrides[e];

                            if (statsOverride == null)
                            {
                                continue;
                            }

                            foreach (var kv in statsOverride)
                            {
                                stats[kv.Key] = kv.Value;
                            }
                        }

                        // sanity check stats
                        foreach (var kv in stats)
                        {
                            if (e.GetMaximumParamValue(kv.Key) < kv.Value)
                            {
                                Console.Error.WriteLine($"{kv.Key} => {kv.Value} for {e} is out of range");
                            }
                        }
                        
                        stats.ForEach(p => StatExprs.AddExprToDict(p.Key, p.Value*gv));
                        
                        // ASSUMPTION: all meldable items have at least one materia slot
                        // ASSUMPTION: customisable relics are unmeldable
                        if (e.FreeMateriaSlots > 0)
                        {
                            CreateMateriaModel(s, e);
                        }
                        else
                        {
                            CreateRelicModel(s, e);
                        }
                    }
                }
            }
        }

        private void CreateRelicModel(EquipSlot s, Equipment e)
        {
            var gv = gear[s, e];

            RelicConfig config = null;
            SolverConfig.RelicConfigs.TryGetValue(e.ItemLevel.Key, out config);

            if (config == null || !config.Items.Contains(e.Key))
            {
                return;
            }

            var isNonSurjective = config.ConversionFunction != null;

            var statCap = config.StatCapOverrides.ContainsKey(Job) && config.StatCapOverrides[Job].ContainsKey(s)
                ? config.StatCapOverrides[Job][s]
                : config.StatCap;

            Model.AddConstraint(
                Expression.Sum(RelevantStats.Select(bp => isNonSurjective ? relicBase[s, e, bp] : cap[s, e, bp])) <=
                statCap*gv,
                $"total relic cap for {e} in slot {s}");
            
            foreach (var bp in RelevantStats)
            {
                var remCap = e.GetMateriaMeldCap(bp, true);
                if (remCap == 0)
                {
                    continue;
                }

                var cv = cap[s, e, bp];

                var func = config.ConversionFunction;

                if (config.ConversionOverride != null && config.ConversionOverride.ContainsKey(bp))
                {
                    func = config.ConversionOverride[bp];
                }

                if (isNonSurjective)
                {
                    func.AddToModel(Model, relicBase[s, e, bp], cv, SolverConfig.SolverSupportsSOS);
                }

                Model.AddConstraint(cv <= remCap*gv, $"upper stat cap for {bp} of relic {e} in slot {s}");
                StatExprs.AddExprToDict(bp, cv);
            }
        }

        private void CreateMateriaModel(EquipSlot s, Equipment e)
        {
            if (!MateriaChoices.Any())
            {
                return;
            }
            var gv = gear[s, e];
            //TODO: you can probably optimize tons here
            Model.AddConstraint(
                Expression.Sum(MateriaChoices.Select(m => materia[s, e, m.Key])) <= e.TotalMateriaSlots()*gv,
                $"restrict total materia amount to amount permitted for {e} in {s}");

            if (e.IsAdvancedMeldingPermitted)
            {
                if (MateriaChoices.Any(m => MainStats.Contains(m.Key.BaseParam.Name)))
                {
                    Model.AddConstraint(
                        Expression.Sum(
                            MateriaChoices.Where(m => MainStats.Contains(m.Key.BaseParam.Name))
                                .Select(m => materia[s, e, m.Key])) <= e.FreeMateriaSlots * gv,
                        $"restrict advanced melding for mainstat materia to amount of slots in {e} in {s}");
                }
                if (MateriaChoices.Any(m => !m.Value))
                {
                    Model.AddConstraint(
                        Expression.Sum(
                            MateriaChoices.Where(m => !m.Value).Select(m => materia[s, e, m.Key])) <=
                        (e.FreeMateriaSlots + SolverConfig.OvermeldThreshold)*gv,
                        $"restrict regular materia amount to amount permitted for {e} in {s}");
                }
            }


            // SIMPLIFICATION: ignoring whether materia fits; doesn't matter anyway
            foreach (var matGrp in MateriaChoices.GroupBy(m => m.Key.BaseParam))
            {
                var bp = matGrp.Key;

                var remCap = e.GetMateriaMeldCap(bp, true);
                if (remCap == 0)
                {
                    continue;
                }

                // we need to constrain against min(remaining cap, melded materia) to account for stat caps
                var cv = cap[s, e, bp];
                Model.AddConstraint(cv <= remCap*gv,
                    $"cap stats using {e}'s meld cap for {bp} in slot {s}");

                var maxRegularMat = matGrp.MaxBy(f => f.Key.Value).Key;
                var maxAdvancedMat = maxRegularMat;
                if (e.IsAdvancedMeldingPermitted)
                {
                    maxAdvancedMat = matGrp.Where(m => m.Value).MaxBy(f => f.Key.Value).Key;
                }

                // need hash-set here for uniqueness
                Model.AddConstraint(
                    cv <=
                    Expression.Sum(
                        new HashSet<MateriaItem> {maxRegularMat, maxAdvancedMat}
                            .Select(m => m.Value*materia[s, e, m])),
                    $"cap stats using used {bp} for {e} in slot {s}");

                StatExprs.AddExprToDict(bp, cv);
            }
        }

        
        private void CreateMateriaOrdering()
        {
            if (!MateriaChoices.Any())
            {
                return;
            }
            
            // collect all equip slots and assign canonical order
            var equipSlots =
                GearChoices.SelectMany(e => e.EquipSlotCategory.PossibleSlots)
                    .DistinctBy(s => s.Name)
                    .OrderBy(s => s.Name)
                    .ToList();
            for (var i = 0; i < equipSlots.Count ; i++)
            {
                var s = equipSlots[i];
                var grp = GearChoices.Where(e => e.EquipSlotCategory.PossibleSlots.Contains(s));
                foreach (var e in grp)
                {
                    MateriaChoices.ForEach(m => materia[s, e, m.Key].BranchingPriority = i);
                }
            }
        }



        /// <summary>
        ///     Apply a given solution. IT IS THE USER'S RESPONSIBILITY TO CHECK THAT THE SOLUTION SUCCEEDED.
        /// </summary>
        /// <param name="sol">Given solution. Only feasible solutions are accepted.</param>
        /// <exception cref="ArgumentException">Thrown if a not feasible solution is passed.</exception>
        /// <exception cref="InvalidOperationException">Thrown if a solution was already applied.</exception>
        public void ApplySolution(Solution sol)
        {
            if (sol.ModelStatus != ModelStatus.Feasible)
            {
                throw new ArgumentException($"Solution status invalid: {sol.ModelStatus}", nameof(sol));
            }

            if (IsSolved)
            {
                throw new InvalidOperationException("Model already solved");
            }

            IsSolved = true;
            Model.VariableCollections.ForEach(vc => vc.SetVariableValues(sol.VariableValues));
            // fix bug with Value non-integer
            Model.Variables.Where(v => v.Type != VariableType.Continuous).ForEach(v => v.Value = Math.Round(v.Value));
            ResultWeight = (Model.Objectives.First().Expression - DummyObjective).Evaluate(sol.VariableValues);
        }

 
    }
}