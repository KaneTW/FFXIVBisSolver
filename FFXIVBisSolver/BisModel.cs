using System;
using System.Collections.Generic;
using System.Linq;
using MoreLinq;
using OPTANO.Modeling.Optimization;
using OPTANO.Modeling.Optimization.Enums;
using OPTANO.Modeling.Optimization.Interfaces;
using OPTANO.Modeling.Optimization.Solver;
using OPTANO.Modeling.Optimization.Solver.Gurobi;
using SaintCoinach.Xiv;
using SaintCoinach.Xiv.Items;

namespace FFXIVBisSolver
{
    public class BisModel
    {
        private const int MaxMateriaSlots = 5;

        private readonly string[] MainStats =
        {
            "Vitality",
            "Strength",
            "Dexterity",
            "Vitality",
            "Intelligence",
            "Mind",
            "Piety"
        };

        /// <summary>
        ///     Creates a new BiS solver model.
        /// </summary>
        /// <param name="weights">Stat weights. The higher the weight, the more a stat is desirable</param>
        /// <param name="statReqs">Minimum amount of stats that must be present in a solution</param>
        /// <param name="baseStats">Stats of a character without any gear</param>
        /// <param name="gearChoices">Gear items to choose from. Keep this small.</param>
        /// <param name="foodChoices">List of food choices. This can be fairly large, it doesn't add much complexity.</param>
        /// <param name="materiaChoices">
        ///     Dictionary of materia choices; set value to true if the materia is allowed for advanced
        ///     melding. The materia with the highest eligible stat value is chosen. (Note that Tier is 0-indexed)
        /// </param>
        /// <param name="relicCaps">Designates customizable relics. Value of an entry determines the total stat cap.</param>
        /// <param name="overmeldThreshold">
        ///     Extend the overmelding threshold --- i.e. if you set overmeldThreshold to n, materia
        ///     from materiaChoices that isn't normally allowed in advanced melds can be used up to n times in advanced meld
        /// </param>
        /// <param name="allocStatCap">Cap for allocatable stats. Default is 35</param>
        /// <param name="maximizeUnweightedValues">Maximize unweighted values with a small weight (1e-5)</param>
        public BisModel(IDictionary<BaseParam, double> weights, IDictionary<BaseParam, int> statReqs,
            IDictionary<BaseParam, int> baseStats, IEnumerable<Equipment> gearChoices, IEnumerable<FoodItem> foodChoices,
            IDictionary<MateriaItem, bool> materiaChoices, IDictionary<Equipment, int> relicCaps = null,
            int overmeldThreshold = 0, int allocStatCap = 35, bool maximizeUnweightedValues = true)
        {
            Model = new Model();

            StatRequirements = statReqs;
            Weights = weights;
            GearChoices = gearChoices.ToList();
            FoodChoices = foodChoices.ToList();
            RelicCaps = relicCaps;
            OvermeldThreshold = overmeldThreshold;
            MaximizeUnweightedValues = maximizeUnweightedValues;

                // collect stats we care about
            RelevantStats = Weights.Keys.Union(StatRequirements.Keys).ToList();


            // we don't care about materia which affect unneeded stats
            MateriaChoices = materiaChoices.Where(m => RelevantStats.Contains(m.Key.BaseParam))
                .ToDictionary(k => k.Key, k => k.Value);

            var allEquipSlots = GearChoices.SelectMany(g => g.EquipSlotCategory.PossibleSlots).ToList();

            gear = new VariableCollection<EquipSlot, Equipment>(Model, allEquipSlots, GearChoices,
                type: VariableType.Binary);
            food = new VariableCollection<FoodItem>(Model, FoodChoices, type: VariableType.Binary);
            foodcap = new VariableCollection<FoodItem,BaseParam>(Model, FoodChoices, RelevantStats, type: VariableType.Integer,
                lowerBoundGenerator: (x,b) => 0);
            materia = new VariableCollection<EquipSlot, Equipment, MateriaItem>(Model, allEquipSlots, GearChoices,
                MateriaChoices.Keys,
                type: VariableType.Integer, lowerBoundGenerator: (s, e, bp) => 0);
            cap = new VariableCollection<EquipSlot, Equipment, BaseParam>(Model, allEquipSlots, GearChoices,
                RelevantStats, type: VariableType.Integer, lowerBoundGenerator: (s, e, b) => 0);

            stat = new VariableCollection<BaseParam>(Model, RelevantStats, type: VariableType.Integer,
                lowerBoundGenerator: x => 0);
            modstat = new VariableCollection<BaseParam>(Model, RelevantStats, type: VariableType.Integer,
                lowerBoundGenerator: x => 0);
            allocstat = new VariableCollection<BaseParam>(Model, RelevantStats, type: VariableType.Integer,
                lowerBoundGenerator: x => 0);

            Model.AddConstraint(Expression.Sum(RelevantStats.Where(bp => MainStats.Contains(bp.Name)).Select(bp => allocstat[bp])) <= allocStatCap, 
                "cap allocatable stats");

            var statExprs = RelevantStats.ToDictionary(bp => bp, bp => Expression.EmptyExpression);
            baseStats.ForEach(kv => statExprs[kv.Key] = kv.Value + Expression.EmptyExpression);


            var foodExprs = RelevantStats.ToDictionary(bp => bp, bp => (Expression)stat[bp]);

            var bigM = 50*
                       GearChoices.Select(
                           g =>
                               g.AllParameters.Select(
                                   p => p.Values.OfType<ParameterValueFixed>().Select(v => v.Amount).Max())
                                   .Max()).Max();

            foreach (var grp in GearChoices.GroupBy(g => g.EquipSlotCategory))
            {
                // if gear is unique, equip it once only.
                grp.Where(e => e.IsUnique)
                    .ForEach(
                        e =>
                            Model.AddConstraint(Expression.Sum(grp.Key.PossibleSlots.Select(s => gear[s, e])) <= 1,
                                $"ensure gear uniqueness for {e}"));

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
                        e.AllParameters.Where(p => RelevantStats.Contains(p.BaseParam))
                            .ForEach(
                                p =>
                                    AddExprToDict(statExprs, p.BaseParam,
                                        p.Values.Sum(v => ((ParameterValueFixed) v).Amount)*gv));

                        // ASSUMPTION: all meldable items have at least one materia slot
                        // ASSUMPTION: customisable relics are unmeldable
                        if (MateriaChoices.Any() && e.FreeMateriaSlots > 0)
                        {
                            var totalSlots = e.IsAdvancedMeldingPermitted ? MaxMateriaSlots : e.FreeMateriaSlots;

                            Model.AddConstraint(
                                Expression.Sum(MateriaChoices.Select(m => materia[s, e, m.Key])) <= totalSlots*gv,
                                $"restrict total materia amount to amount permitted for {e} in {s}");

                            if (e.IsAdvancedMeldingPermitted)
                            {
                                if (MateriaChoices.Any(m => MainStats.Contains(m.Key.BaseParam.Name)))
                                {
                                    Model.AddConstraint(
                                        Expression.Sum(
                                            MateriaChoices.Where(m => MainStats.Contains(m.Key.BaseParam.Name))
                                                .Select(m => materia[s, e, m.Key])) <= e.FreeMateriaSlots,
                                        $"restrict advanced melding for mainstat materia to amount of slots in {e} in {s}");
                                }
                                if (MateriaChoices.Any(m => !m.Value))
                                {
                                    Model.AddConstraint(
                                        Expression.Sum(
                                            MateriaChoices.Where(m => !m.Value).Select(m => materia[s, e, m.Key])) <=
                                        e.FreeMateriaSlots + OvermeldThreshold,
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
                                MateriaItem maxAdvancedMat = maxRegularMat;
                                if (e.IsAdvancedMeldingPermitted)
                                {
                                    maxAdvancedMat = matGrp.Where(m => m.Value).MaxBy(f => f.Key.Value).Key;
                                }

                                // need hash-set here for uniqueness
                                Model.AddConstraint(
                                    cv <=
                                    Expression.Sum(
                                        new HashSet<MateriaItem> {maxRegularMat, maxAdvancedMat}
                                            .Select<MateriaItem, Term>(
                                                m => m.Value*materia[s, e, m])),
                                    $"cap stats using used {bp} for {e} in slot {s}");

                                AddExprToDict(statExprs, bp, cv);
                            }
                        }
                        else if (RelicCaps != null && RelicCaps.ContainsKey(e))
                        {
                            var eCap = RelicCaps[e];
                            Model.AddConstraint(Expression.Sum(RelevantStats.Select(bp => cap[s, e, bp])) <= eCap*gv,
                                $"total relic cap for {e} in slot {s}");
                            foreach (var bp in RelevantStats)
                            {
                                var remCap = e.GetMateriaMeldCap(bp, true);
                                if (remCap == 0)
                                {
                                    continue;
                                }

                                var cv = cap[s, e, bp];

                                Model.AddConstraint(cv <= remCap*gv, $"upper stat cap for {bp} of relic {e} in slot {s}");
                                AddExprToDict(statExprs, bp, cv);
                                // SIMPLIFICATION: impossible-to-reach stat values are ignored. Can be handled by using Model.AddAlternativeConstraint(cv <= badVal - 1, cv >= badVal +1, bigM)
                            }
                        }
                    }
                }
            }

            // avoid trivial constraints
            if (FoodChoices.Any())
            {
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
                            AddExprToDict(statExprs, bp, pval.Amount*fv);
                        }

                        foreach (var pval in pvals.OfType<ParameterValueRelative>())
                        {
                            // add relative modifier stat[bp]
                            Model.AddConstraint(foodcap[itm, bp] <= pval.Amount*stat[bp],
                                $"relative modifier for food {fd} in slot {bp}}}");

                            var limited = pval as ParameterValueRelativeLimited;
                            if (limited != null)
                            {
                                Model.AddConstraint(foodcap[itm, bp] <= limited.Maximum*fv,
                                    $"cap for relative modifier for food {fd} in slot {bp}");
                            }

                            AddExprToDict(foodExprs, bp, foodcap[itm,bp]);
                        }
                    }
                }
            }

            var objExpr = Expression.EmptyExpression;
            foreach (var bp in RelevantStats)
            {
                if (MainStats.Contains(bp.Name))
                {
                    AddExprToDict(statExprs, bp, allocstat[bp]);
                }

                Model.AddConstraint(stat[bp] == statExprs[bp], "set collected stat " + bp);
                Model.AddConstraint(modstat[bp] <= foodExprs[bp], "relative food bonuses for " + bp);

                if (Weights.ContainsKey(bp))
                {
                    objExpr += Weights[bp]*modstat[bp];
                }

                if (StatRequirements.ContainsKey(bp))
                {
                    if (MaximizeUnweightedValues && !Weights.ContainsKey(bp))
                    {
                        DummyObjective += modstat[bp] * 1e-5;
                    }
                    Model.AddConstraint(modstat[bp] >= StatRequirements[bp], "satisfy stat requirement for " + bp);
                }
            }
            Model.AddObjective(new Objective(objExpr + DummyObjective, "stat weight", ObjectiveSense.Maximize), "stat weight");
        }

        public bool MaximizeUnweightedValues { get; }

        public Model Model { get; }
        public IDictionary<BaseParam, double> Weights { get; }
        public IDictionary<BaseParam, int> StatRequirements { get; }
        public IList<Equipment> GearChoices { get; }
        public IList<FoodItem> FoodChoices { get; }
        public IDictionary<MateriaItem, bool> MateriaChoices { get; }
        public int OvermeldThreshold { get; }
        public IList<BaseParam> RelevantStats { get; }
        public IDictionary<Equipment, int> RelicCaps { get; }


        public VariableCollection<BaseParam> stat { get; }
        public VariableCollection<BaseParam> modstat { get; }
        public VariableCollection<BaseParam> allocstat { get; }
        public VariableCollection<EquipSlot, Equipment> gear { get; }
        public VariableCollection<FoodItem> food { get; }
        public VariableCollection<FoodItem, BaseParam> foodcap { get; }
        public VariableCollection<EquipSlot, Equipment, MateriaItem> materia { get; }
        public VariableCollection<EquipSlot, Equipment, BaseParam> cap { get; }
        

        public IEnumerable<Equipment> ChosenGear
        {
            get { return VarCollToDict(gear).Where(kv => kv.Key.Value > 0).Select(kv => (Equipment) kv.Value[1]); }
        }

        public FoodItem ChosenFood
        {
            get { return (FoodItem) VarCollToDict(food).FirstOrDefault(kv => kv.Key.Value > 0).Value[0]; }
        }

        public IEnumerable<Tuple<EquipSlot, Equipment, MateriaItem, int>> ChosenMateria
        {
            get
            {
                return
                    VarCollToDict(materia)
                        .Where(kv => kv.Key.Value > 0)
                        .Select(
                            kv =>
                                Tuple.Create((EquipSlot) kv.Value[0], (Equipment) kv.Value[1], (MateriaItem) kv.Value[2],
                                    Convert.ToInt32(kv.Key.Value)));
            }
        }

        public IEnumerable<Tuple<EquipSlot, BaseParam, int>> ChosenRelicStats
        {
            get
            {
                return
                    VarCollToDict(cap)
                        .Where(
                            kv => kv.Key.Value > 0 &&
                                  ChosenGear.Contains((Equipment) kv.Value[1]) &&
                                  RelicCaps.ContainsKey((Equipment) kv.Value[1]))
                        .Select(
                            kv =>
                                Tuple.Create((EquipSlot) kv.Value[0], (BaseParam) kv.Value[2],
                                    Convert.ToInt32(kv.Key.Value)));
            }
        }

        private Dictionary<BaseParam, int> GetResultStat(VariableCollection<BaseParam> stat)
        {
            return VarCollToDict(stat)
                .ToDictionary(kv => (BaseParam) kv.Value[0], kv => Convert.ToInt32(kv.Key.Value));

        }

        private Expression DummyObjective { get; set; } = Expression.EmptyExpression;

        public Dictionary<BaseParam, int> ResultGearStats => GetResultStat(stat);

        public Dictionary<BaseParam, int> ResultTotalStats => GetResultStat(modstat);

        public Dictionary<BaseParam, int> ResultAllocatableStats => GetResultStat(allocstat);

        public double ResultWeight { get; private set; }

        public bool IsSolved { get; private set; }

        private static void AddExprToDict<T>(Dictionary<T, Expression> dict, T k, Expression expr)
        {
            if (dict.ContainsKey(k))
            {
                var old = dict[k];
                dict.Remove(k);
                dict.Add(k, old + expr);
            }
            else
            {
                dict.Add(k, expr);
            }
        }

        /// <summary>
        /// Apply a given solution. IT IS THE USER'S RESPONSIBILITY TO CHECK THAT THE SOLUTION SUCCEEDED.
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
            ResultWeight = (Model.Objectives.First().Expression - DummyObjective).Evaluate(sol.VariableValues);
        }

        private static Dictionary<Variable, object[]> VarCollToDict(IVariableCollection coll)
        {
            return coll.Variables.Zip(coll.ExistingIndices, (k, v) => new {Key = k, Value = v})
                .ToDictionary(x => x.Key, x => x.Value);
        }
    }
}