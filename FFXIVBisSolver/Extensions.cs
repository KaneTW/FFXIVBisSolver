using System;
using System.Collections.Generic;
using System.Linq;
using OPTANO.Modeling.Optimization;
using OPTANO.Modeling.Optimization.Enums;
using OPTANO.Modeling.Optimization.Interfaces;
using SaintCoinach.Xiv;
using SaintCoinach.Xiv.Items;

namespace FFXIVBisSolver
{
    public static class OptanoExtensions
    {

        public static int TotalMateriaSlots(this Equipment e)
        {
            return e.IsAdvancedMeldingPermitted ? BisModel.MaxMateriaSlots : e.FreeMateriaSlots;
        }

        public static Dictionary<Variable, object[]> VarCollToDict(this IVariableCollection coll)
        {
            return coll.Variables.Zip(coll.ExistingIndices, (k, v) => new {Key = k, Value = v})
                .ToDictionary(x => x.Key, x => x.Value);
        }

        public static Dictionary<BaseParam, int> GetResultStat(this VariableCollection<BaseParam> stat)
        {
            return stat.VarCollToDict().ToDictionary(kv => (BaseParam) kv.Value[0], kv => Convert.ToInt32(kv.Key.Value));
        }

        public static void AddExprToDict<T>(this Dictionary<T, Expression> dict, T k, Expression expr)
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

        public static void AddSOS2(this Model model, Dictionary<Variable, double> vars, bool useWorkaround)
        {
            if (useWorkaround)
            {
                model.AddSOS2(vars);
            }
            else
            {
                var ordered = vars.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
                var range = Enumerable.Range(0, ordered.Count - 1).ToList();
                var segmentVars = new VariableCollection<int>(model, range, lowerBoundGenerator: x => 0);
                var binaryVars = new VariableCollection<int>(model, range, type: VariableType.Binary);

                model.AddConstraint(Expression.Sum(range.Select(i => binaryVars[i])) == 1);
                range.ForEach(i => model.AddConstraint(segmentVars[i] <= binaryVars[i]));


                model.AddConstraint(ordered[0] == binaryVars[0] - segmentVars[0]);
                model.AddConstraint(ordered[ordered.Count - 1] == (Expression)segmentVars[ordered.Count - 2]);

                for (var i = 0; i < ordered.Count; i++)
                {
                    var expr = Expression.EmptyExpression;
                    if (i > 0)
                    {
                        expr += segmentVars[i - 1];
                    }

                    if (i < ordered.Count - 1)
                    {
                        expr += binaryVars[i] - segmentVars[i];
                    }

                    model.AddConstraint(ordered[i] == expr);
                }
            }
        }
    }
}