using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.CommandLineUtils;
using YamlDotNet;

using FFXIVBisSolver;
using OPTANO.Modeling.Common;
using OPTANO.Modeling.Optimization;
using OPTANO.Modeling.Optimization.Enums;
using OPTANO.Modeling.Optimization.Solver.GLPK;
using OPTANO.Modeling.Optimization.Solver.Gurobi702;
using OPTANO.Modeling.Optimization.Solver.Z3;
using SaintCoinach;
using SaintCoinach.Ex;
using SaintCoinach.Xiv;
using SaintCoinach.Xiv.Items;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.ObjectFactories;

namespace FFXIVBisSolverCLI
{
    public class AppConfig
    {
        public Dictionary<ClassJob, JobConfig> JobConfigs { get; set; }
        public Dictionary<int, int> RelicCaps { get; set; }
        public Dictionary<BaseParam, int> BaseStats { get; set; }
    }

    public class JobConfig
    {
        public Dictionary<BaseParam, double> Weights { get; set; }
        public Dictionary<BaseParam, int> StatRequirements { get; set; }
        
    }
    
    public class Program
    {
        //TODO: make this more pretty
        public static void Main(string[] args)
        {
            var cliApp = new CommandLineApplication();
            var xivPathOpt = cliApp.Option("-p |--game-path <pathToFFXIV>",
                "Path to the FFXIV game install (folder containing boot and game)", CommandOptionType.SingleValue);

            var configOpt = cliApp.Option("-c |--config-path <pathToYaml>",
                "Path to configuration YAML file, default to config.yaml", CommandOptionType.SingleValue);

            var excludedOpt = cliApp.Option("-X |--exclude <itemId>",
                "Item ids of items to exclude from solving", CommandOptionType.MultipleValue);

            var minIlvlOpt = cliApp.Option("-m |--min-itemlevel <ilvl>",
                "Minimum item level of items to consider. Uses max-20 if not passed.", CommandOptionType.SingleValue);
            var maxIlvlOpt = cliApp.Option("-M |--max-itemlevel <ilvl>",
                "Maximum item level of items to consider", CommandOptionType.SingleValue);

            var maxOvermeldTierOpt = cliApp.Option("-T |--max-overmeld-tier <tier>",
                "The max tier of materia allowed for overmelds", CommandOptionType.SingleValue);

            var solverOpt = cliApp.Option("-s |--solver <solver>", "Solver to use (default: GLPK)", CommandOptionType.SingleValue);

            var debugOpt = cliApp.Option("-d |--debug", "Print the used models in the current directory as model.lp",
                CommandOptionType.NoValue);

            var jobArg = cliApp.Argument("<job>", "Enter the job abbreviation to solve for");

            cliApp.HelpOption("-h |--help");

            cliApp.OnExecute(() =>
            {
                if (jobArg.Value == null)
                {
                    Console.Error.WriteLine("You must provide a job to solve for.");
                    return 1;
                }

                if (!xivPathOpt.HasValue())
                {
                    Console.Error.WriteLine("You must provide a path to FFXIV!");
                    return 1;
                }

                var realm = new ARealmReversed(xivPathOpt.Value(), Language.English);
                var xivColl = realm.GameData;

                var deserializer = new DeserializerBuilder()
                    .WithTypeConverter(new BaseParamConverter(xivColl))
                    .WithTypeConverter(new ClassJobConverter(xivColl))
                    .WithNamingConvention(new CamelCaseNamingConvention())
                    .Build();
                
                AppConfig config = null;

                using (var s = new FileStream(configOpt.HasValue() ? configOpt.Value() : "config.yaml", FileMode.Open))
                {
                    config = deserializer.Deserialize<AppConfig>(new StreamReader(s));
                }

                var classJob = xivColl.GetSheet<ClassJob>().Single(x => x.Abbreviation == jobArg.Value);

                var jobConfig = config.JobConfigs[classJob];

                List<Item> items = xivColl.GetSheet<Item>().ToList();

                if (excludedOpt.HasValue())
                {
                    List<int> excludedIds = new List<int>();
                    foreach (var excluded in excludedOpt.Values)
                    {
                        var id = int.Parse(excluded);
                        var item = xivColl.Items[id];
                        if (item != null)
                        {
                            Console.WriteLine($"Excluding {item}.");
                            excludedIds.Add(id);
                        }
                        else
                        {
                            Console.Error.WriteLine($"Unknown id {id}, ignoring.");
                        }
                    }
                    items = items.Where(k => !excludedIds.Contains(k.Key)).ToList();
                }

                var equip = items.OfType<Equipment>().Where(e => e.ClassJobCategory.ClassJobs.Contains(classJob));
                
                var maxIlvl = equip.Max(x => x.ItemLevel.Key);
                if (maxIlvlOpt.HasValue())
                {
                    maxIlvl = int.Parse(maxIlvlOpt.Value());
                }

                var minIlvl = maxIlvl - 20;
                if (minIlvlOpt.HasValue())
                {
                    minIlvl = int.Parse(minIlvlOpt.Value());

                }

                equip = equip.Where(e => e.ItemLevel.Key >= minIlvl && e.ItemLevel.Key <= maxIlvl).ToList();

                var food = items.Where(FoodItem.IsFoodItem).Select(t => new FoodItem(t));

                var materia = items.OfType<MateriaItem>()
                    .ToDictionary(i => i,
                        i => !maxOvermeldTierOpt.HasValue() || i.Tier < int.Parse(maxOvermeldTierOpt.Value()));

                var relicCaps =
                    equip.Where(e => config.RelicCaps.ContainsKey(e.ItemLevel.Key))
                        .ToDictionary(e => e, e => config.RelicCaps[e.ItemLevel.Key]);

                //TODO: improve solver handling
                SolverBase solver = new GLPKSolver();
                if (solverOpt.HasValue())
                {
                    switch (solverOpt.Value())
                    {
                        case "Gurobi":
                            solver = new GurobiSolver();
                            break;
                        case "Z3":
                            solver = new Z3Solver();
                            break;
                    }
                }


                using (var scope = new ModelScope())
                {
                    var model = new BisModel(jobConfig.Weights, jobConfig.StatRequirements, config.BaseStats,
                        equip, food, materia, relicCaps);

                    if (debugOpt.HasValue())
                    {
                        using (var f = new FileStream("model.lp", FileMode.Create))
                        {
                            model.Model.Objective.Expression = model.Model.Objective.Expression.Normalize();
                            model.Model.Constraints.ForEach(c => c.Expression = c.Expression.Normalize());
                            model.Model.Write(f, FileType.LP);
                        }
                    }

                    var solution = solver.Solve(model.Model);
                    model.ApplySolution(solution);
                    Console.WriteLine("Gear: ");
                    model.ChosenGear.ForEach(Console.WriteLine);
                    Console.WriteLine("Materia: ");
                    model.ChosenMateria.ForEach(Console.WriteLine);
                    if (model.ChosenRelicStats.Any())
                    {
                        Console.WriteLine("Relic stats: ");
                        model.ChosenRelicStats.ForEach(Console.WriteLine);
                    }
                    Console.WriteLine("Food: ");
                    Console.WriteLine(model.ChosenFood);
                    Console.WriteLine("Allocated stats: ");
                    model.ResultAllocatableStats.ForEach(kv => Console.WriteLine(kv));
                    Console.WriteLine("Result stats with food:");
                    model.ResultTotalStats.ForEach(kv => Console.WriteLine(kv));
                    Console.WriteLine($"Result stat weight: {model.Model.Objectives.First().Expression.Evaluate(solution.VariableValues)}");
                }

                return 0;
            });

            cliApp.Execute(args);
        }
    }
}
