using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Scoring;

namespace PerformanceCalculator.Precalc
{
    [Command(Name = "precalc", Description = "Computes all possible permutations of mods for .osu files in a given folder.")]
    public class PrecalcCommand : ProcessorCommand
    {
        [UsedImplicitly]
        [Required, DirectoryExists]
        [Argument(0, Description = "Required. A folder containing .osu files")]
        public string OsuFilesFolder { get; }

        [UsedImplicitly]
        [Required]
        [Argument(1, Description = "Required. Output file for csv results.")]
        public string CsvFilePath { get; }

        public class BeatmapCsvInfo
        {
            public int BeatmapId { get; set; }
            public bool HR { get; set; }
            public bool EZ { get; set; }
            public bool FL { get; set; }
            public bool DT { get; set; }
            public bool HT { get; set; }
            public bool HD { get; set; }
            public double LazerSR { get; set; }
            public double LazerPP { get; set; }
        }

        private static BeatmapCsvInfo makeCsvInfo(int BeatmapId, PerformanceAttributes performance, DifficultyAttributes difficulty, Mod[] mods)
        {
            var res = new BeatmapCsvInfo
            {
                BeatmapId = BeatmapId,
                LazerPP = performance?.Total ?? 0.0,
                LazerSR = difficulty.StarRating
            };

            // iterate through the mods. if a mod is found that fits the member variable of BeatmapCsvInfo, make it true

            foreach (var mod in mods)
            {
                if (mod is OsuModEasy) res.EZ = true;
                if (mod is OsuModHardRock) res.HR = true;
                if (mod is OsuModFlashlight) res.FL = true;
                if (mod is OsuModDoubleTime) res.DT = true;
                if (mod is OsuModHalfTime) res.HT = true;
                if (mod is OsuModHidden) res.HD = true;
            }

            return res;
        }

        private bool worker(object state)
        {

            var workerParams = (WorkerParams)state;


            var workingBeatmap = ProcessorWorkingBeatmap.FromFileOrId(workerParams.File);

            // check for gamemode
            if (workingBeatmap.BeatmapInfo.Ruleset.ShortName != "osu")
            {
                Console.WriteLine($"Map {workingBeatmap} isnt osu!std, but {workingBeatmap.BeatmapInfo.Ruleset.ShortName}");
                return true;
            }

            Console.WriteLine($"Working on {workingBeatmap} with {workerParams.Combo.Aggregate("", (s, mod) => s + mod.Acronym + ",")}");

            Mod[] modsToUse = workerParams.Combo.ToArray();

            // assume 100% acc, and thats it
            var scoreInfo = new ScoreInfo
            {
                Accuracy = 1.0,
                Mods = modsToUse,
            };
            var score = new ProcessorScoreDecoder(workingBeatmap).Parse(scoreInfo);


            var difficultyCalculator = workerParams.Ruleset.CreateDifficultyCalculator(workingBeatmap);
            var difficultyAttributes = difficultyCalculator.Calculate(modsToUse);



            score.ScoreInfo.MaxCombo = difficultyAttributes.MaxCombo;

            var performanceCalculator = workerParams.Ruleset.CreatePerformanceCalculator();

            var ppAttributes = performanceCalculator.Calculate(score.ScoreInfo, difficultyAttributes);

            var csvStuff = makeCsvInfo(workingBeatmap.BeatmapInfo.OnlineID, ppAttributes, difficultyAttributes, modsToUse);

            workerParams.Writer.WriteRecord(csvStuff);
            workerParams.Writer.NextRecord();

            return false;
        }

        private class WorkerParams
        {
            public string File { get; set; }
            public Mod[] Combo { get; set; }
            public Ruleset Ruleset { get; set; }
            public CsvWriter Writer { get; set; }


        }

        public override void Execute()
        {
            // make the csv file writer
            var csvConfig = new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
            {
                NewLine = Environment.NewLine,
                Delimiter = ",",
                BufferSize = 1024 * 1024 * 32, // 32mb buffer
            };

            ThreadPool.SetMaxThreads(Environment.ProcessorCount, Environment.ProcessorCount * 4);
            ThreadPool.SetMinThreads(Environment.ProcessorCount / 2, Environment.ProcessorCount);

            var records = new List<BeatmapCsvInfo>();           

            // Get a list of all .osu files in the OsuFilesFolder
            var allFiles = Directory.GetFiles(OsuFilesFolder, "*.osu", SearchOption.AllDirectories);

            var ruleset = LegacyHelper.GetRulesetFromLegacyID(0); // 0 is std
            var availableMods = ruleset.CreateAllMods();

            var legacyMods = LegacyHelper.ConvertToLegacyDifficultyAdjustmentMods(ruleset, availableMods.ToArray());
            // remove the classic mod, and nightcore (as it covers doubletime)
            var cleanedLegacyMods = removeMods(legacyMods, new List<Type>{
                typeof(OsuModClassic),
                typeof(OsuModNightcore),
                typeof(OsuModTouchDevice),
                typeof(OsuModFlashlight),
                typeof(OsuModHidden),
                typeof(OsuModEasy),
                typeof(OsuModHalfTime)
            });

            var permutatedModCombos = permutateMods(cleanedLegacyMods);
            // filter out the following Combos:
            // HR + EZ
            // DT + HT
            permutatedModCombos = permutatedModCombos.Where(x => !(x.Contains(availableMods.First(y => y.Acronym == "HR")) && x.Contains(availableMods.First(y => y.Acronym == "EZ")))).ToList();
            permutatedModCombos = permutatedModCombos.Where(x => !(x.Contains(availableMods.First(y => y.Acronym == "DT")) && x.Contains(availableMods.First(y => y.Acronym == "HT")))).ToList();

            var writer = new StreamWriter(CsvFilePath);
            var csv = new CsvWriter(writer, csvConfig);

            csv.WriteHeader<BeatmapCsvInfo>();
            csv.NextRecord();

            Stopwatch watch = new Stopwatch();
            watch.Start();
            
            foreach (var file in allFiles)
            {
                bool shouldExclude = false;
                foreach (var combo in permutatedModCombos)
                {
                    var workerParams = new WorkerParams
                    {
                        Combo = combo.ToArray(),
                        File = file,
                        Ruleset = ruleset
                    };

                    workerParams.Writer = csv;

                    shouldExclude = worker(workerParams);

                    if (shouldExclude)
                    {
                        break;
                    }
                }
                
                if (shouldExclude)
                {
                    Console.WriteLine($"Excluding {file} because it is not osu!std");
                    continue;
                }
            }

            csv.Flush();

            watch.Stop();
            Console.WriteLine($"Took {watch.Elapsed.TotalSeconds} seconds");
        }

        private IEnumerable<Mod> removeMods(IEnumerable startMods, IEnumerable<Type> typesToRemove)
        {
            foreach (var mod in startMods)
            {
                if (!typesToRemove.Contains(mod.GetType()))
                    yield return (Mod)mod;
            }
        }
        
        private List<List<Mod>> permutateMods(IEnumerable<Mod> mods)
        {
            var result = new List<List<Mod>>();
            

            // For each mod
            foreach (var mod in mods)
            {
                // Add the mod to the list of mod combinations
                result.Add(new List<Mod> { mod });

                // For each mod combination
                foreach (var modCombo in result.ToList())
                {
                    // If the mod is already in the mod combination, skip it
                    if (modCombo.Contains(mod))
                        continue;

                    // Add the mod to the mod combination
                    var newModCombo = modCombo.ToList();
                    newModCombo.Add(mod);

                    // Add the new mod combination to the list of mod combinations
                    result.Add(newModCombo);
                }
            }

            result.Add(new List<Mod>());
            
            return result;
        }
    }
}
