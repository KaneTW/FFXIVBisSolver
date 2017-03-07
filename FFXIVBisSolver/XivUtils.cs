using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MoreLinq;

namespace FFXIVBisSolver
{
    public class XivUtils
    {
        public static PiecewiseLinearFunction CreateSSTiers(List<double> baseCastTimes, List<double> castTimeBuffs,
            int minStat, int count)
        {
            if (castTimeBuffs == null || !castTimeBuffs.Any())
            {
                castTimeBuffs = new List<double> {0};
            }
            
            var map =
                Enumerable.Range(minStat, count)
                    .Select(
                        x =>
                            new
                            {
                                SS = x,
                                GCD =
                                    baseCastTimes.SelectMany(
                                        baseCast => castTimeBuffs.Select(buff => CalculateCastTime(baseCast, buff, x))).ToHashSet()
                            });
            return new PiecewiseLinearFunction(map.GroupBy(k => k.GCD, comparer: HashSet<double>.CreateSetComparer())
                .Select(grp => new {Min = grp.Min(t => t.SS), Max = grp.Max(t => t.SS)})
                .SelectMany(x => new[] {Tuple.Create(x.Min, x.Min), Tuple.Create(x.Max+1, x.Min)}));
        }

        private static double RoundDown(double x, int d)
        {
            var fac = Math.Pow(10, d);
            return Math.Floor(x*fac)/fac;
        }
        private static double CalculateCastTime(double baseCast, double buff, int ss)
        {
            return RoundDown(RoundDown((1 - RoundDown((ss - 354)*(0.13/858), 3))*baseCast, 3)*(1-buff), 2);
        }
    }
}
