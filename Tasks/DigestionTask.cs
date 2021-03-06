﻿using Engine;
using Proteomics;
using Proteomics.ProteolyticDigestion;
using Proteomics.RetentionTimePrediction;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UsefulProteomicsDatabases;

namespace Tasks
{
    public class DigestionTask : ProteaseGuruTask
    {
        public DigestionTask(): base(MyTask.Digestion)
        { 
          DigestionParameters = new Parameters();
        }
        public static event EventHandler<StringEventArgs> DigestionWarnHandler;
        public Parameters DigestionParameters { get; set; }

        public Dictionary<string, Dictionary<string, Dictionary<Protein, List<InSilicoPep>>>> PeptideByFile;


        public override MyTaskResults RunSpecific(string OutputFolder, List<DbForDigestion> dbFileList)
        {                    
            PeptideByFile =
                new Dictionary<string, Dictionary<string, Dictionary<Protein, List<InSilicoPep>>>>(dbFileList.Count);
            foreach (var database in dbFileList)
            {
                PeptideByFile.Add(database.FileName, new Dictionary<string, Dictionary<Protein, List<InSilicoPep>>>(DigestionParameters.ProteasesForDigestion.Count));
                Dictionary<string, Dictionary<Protein, List<InSilicoPep>>> peptidesByProtease = new Dictionary<string, Dictionary<Protein, List<InSilicoPep>>>();
                List<Protein> proteins = LoadProteins(database);
                foreach (var protease in DigestionParameters.ProteasesForDigestion)
                {                    
                    PeptideByFile[database.FileName].Add(protease.Name, DeterminePeptideStatus(database.FileName, DigestDatabase(proteins, protease, DigestionParameters), DigestionParameters));                   
                }                
            }
            WritePeptidesToTsv(PeptideByFile, OutputFolder, DigestionParameters);
            MyTaskResults myRunResults = new MyTaskResults(this);
            return myRunResults;
        }
        // Load proteins from XML or FASTA databases and keep them associated with the database file name from which they came from
        protected List<Protein> LoadProteins(DbForDigestion database)
        {                        
                List<string> dbErrors = new List<string>();
                List<Protein> proteinList = new List<Protein>();
                
                string theExtension = Path.GetExtension(database.FilePath).ToLowerInvariant();
                bool compressed = theExtension.EndsWith("gz"); // allows for .bgz and .tgz, too which are used on occasion
                theExtension = compressed ? Path.GetExtension(Path.GetFileNameWithoutExtension(database.FilePath)).ToLowerInvariant() : theExtension;

                if (theExtension.Equals(".fasta") || theExtension.Equals(".fa"))
                {
                    proteinList = ProteinDbLoader.LoadProteinFasta(database.FilePath, true, DecoyType.None, false, ProteinDbLoader.UniprotAccessionRegex,
                        ProteinDbLoader.UniprotFullNameRegex, ProteinDbLoader.UniprotFullNameRegex, ProteinDbLoader.UniprotGeneNameRegex,
                        ProteinDbLoader.UniprotOrganismRegex, out dbErrors, -1);
                    if (!proteinList.Any())
                    {
                        Warn("Warning: No protein entries were found in the database");
                        return new List<Protein>() { };
                    }
                    else
                    {
                        return proteinList;
                    }

                }
                else
                {
                    List<string> modTypesToExclude = new List<string> { };
                    proteinList = ProteinDbLoader.LoadProteinXML(database.FilePath, true, DecoyType.None, GlobalVariables.AllModsKnown, false, modTypesToExclude,
                        out Dictionary<string, Modification> um, -1, 4, 1);
                    if (!proteinList.Any())
                    {
                        Warn("Warning: No protein entries were found in the database");
                        return new List<Protein>() { };
                }
                    else
                    {
                        return proteinList;
                    }
                }
            
            
        }
        //digest proteins for each database using the protease and settings provided
        protected Dictionary<Protein, List<PeptideWithSetModifications>> DigestDatabase(List<Protein> proteinsFromDatabase,
            Protease protease, Parameters userDigestionParams)
        {           
            DigestionParams dp = new DigestionParams(protease: protease.Name, maxMissedCleavages: userDigestionParams.NumberOfMissedCleavagesAllowed,
                minPeptideLength: userDigestionParams.MinPeptideLengthAllowed, maxPeptideLength: userDigestionParams.MaxPeptideLengthAllowed);            
            Dictionary<Protein, List<PeptideWithSetModifications>> peptidesForProtein = new Dictionary<Protein, List<PeptideWithSetModifications>>(proteinsFromDatabase.Count);
            foreach (var protein in proteinsFromDatabase)
            {
                List<PeptideWithSetModifications> peptides = protein.Digest(dp, new List<Modification> { }, new List<Modification> { }).ToList();
                peptidesForProtein.Add(protein, peptides);
            }
            return peptidesForProtein;
        }        

        
        Dictionary<Protein, List<InSilicoPep>> DeterminePeptideStatus(string databaseName, Dictionary<Protein, List<PeptideWithSetModifications>> databasePeptides, Parameters userParams)
        {
            SSRCalc3 RTPrediction = new SSRCalc3("SSRCalc 3.0 (300A)", SSRCalc3.Column.A300);
            Dictionary<Protein, List<InSilicoPep>> inSilicoPeptides = new Dictionary<Protein, List<InSilicoPep>>();
            if (userParams.TreatModifiedPeptidesAsDifferent == true)
            {
                foreach (var peptideSequence in databasePeptides.Select(p => p.Value).SelectMany(pep => pep).GroupBy(p => p.FullSequence).ToDictionary(group => group.Key, group => group.ToList()))
                {
                    if (peptideSequence.Value.Select(p => p.Protein).Distinct().Count() == 1)
                    {
                        foreach (var peptide in peptideSequence.Value)
                        {                          
                            
                            if (inSilicoPeptides.ContainsKey(peptide.Protein))
                            {
                                inSilicoPeptides[peptide.Protein].Add(new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, true, RTPrediction.ScoreSequence(peptide), GetCifuentesMobility(peptide), peptide.Length, peptide.MonoisotopicMass, databaseName,
                                    peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name));
                            }
                            else
                            {
                                inSilicoPeptides.Add(peptide.Protein, new List<InSilicoPep>() { new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, true, RTPrediction.ScoreSequence(peptide), GetCifuentesMobility(peptide), peptide.Length, peptide.MonoisotopicMass, databaseName,
                                peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name)});
                            }

                        }
                    }
                    else
                    {
                        foreach (var peptide in peptideSequence.Value)
                        {
                            
                            if (inSilicoPeptides.ContainsKey(peptide.Protein))
                            {
                                inSilicoPeptides[peptide.Protein].Add(new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, false, RTPrediction.ScoreSequence(peptide), GetCifuentesMobility(peptide), peptide.Length, peptide.MonoisotopicMass,databaseName,
                                    peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name));
                            }
                            else
                            {
                                inSilicoPeptides.Add(peptide.Protein, new List<InSilicoPep>() { new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, false, RTPrediction.ScoreSequence(peptide), GetCifuentesMobility(peptide), peptide.Length, peptide.MonoisotopicMass, databaseName,
                                peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name)});
                            }

                        }
                    }
                }
            }
            else 
            {
                foreach (var peptideSequence in databasePeptides.Select(p => p.Value).SelectMany(pep => pep).GroupBy(p => p.BaseSequence).ToDictionary(group => group.Key, group => group.ToList()))
                {
                    if (peptideSequence.Value.Select(p => p.Protein).Distinct().Count() == 1)
                    {
                        foreach (var peptide in peptideSequence.Value)
                        {
                            var hydrophob = RTPrediction.ScoreSequence(peptide);
                            var em = GetCifuentesMobility(peptide);
                            if (inSilicoPeptides.ContainsKey(peptide.Protein))
                            {
                                inSilicoPeptides[peptide.Protein].Add(new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, true, hydrophob, em, peptide.Length, peptide.MonoisotopicMass, databaseName,
                                    peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name));
                            }
                            else
                            {
                                inSilicoPeptides.Add(peptide.Protein, new List<InSilicoPep>() { new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, true, hydrophob, em, peptide.Length, peptide.MonoisotopicMass, databaseName,
                                peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name)});
                            }

                        }
                    }
                    else
                    {
                        foreach (var peptide in peptideSequence.Value)
                        {
                            var hydrophob = RTPrediction.ScoreSequence(peptide);
                            var em = GetCifuentesMobility(peptide);
                            if (inSilicoPeptides.ContainsKey(peptide.Protein))
                            {
                                inSilicoPeptides[peptide.Protein].Add(new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, false, hydrophob, em, peptide.Length, peptide.MonoisotopicMass, databaseName,
                                    peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name));
                            }
                            else
                            {
                                inSilicoPeptides.Add(peptide.Protein, new List<InSilicoPep>() { new InSilicoPep(peptide.BaseSequence, peptide.FullSequence, peptide.PreviousAminoAcid, peptide.NextAminoAcid, false, hydrophob, em, peptide.Length, peptide.MonoisotopicMass, databaseName,
                                peptide.Protein.Accession, peptide.OneBasedStartResidueInProtein, peptide.OneBasedEndResidueInProtein, peptide.DigestionParams.Protease.Name)});
                            }

                        }
                    }
                }
            }
            databasePeptides = null;
            return inSilicoPeptides;
        }

        private static double GetCifuentesMobility(PeptideWithSetModifications pwsm)
        {
            int charge = 1 + pwsm.BaseSequence.Count(f => f == 'K') + pwsm.BaseSequence.Count(f => f == 'R') + pwsm.BaseSequence.Count(f => f == 'H') - CountModificationsThatShiftMobility(pwsm.AllModsOneIsNterminus.Values.AsEnumerable());// the 1 + is for N-terminal

            double mobility = (Math.Log(1 + 0.35 * (double)charge)) / Math.Pow(pwsm.MonoisotopicMass, 0.411);
            if (Double.IsNaN(mobility)==true)
            {
                mobility = 0;
            }
            return mobility;
        }
        public static int CountModificationsThatShiftMobility(IEnumerable<Modification> modifications)
        {
            List<string> shiftingModifications = new List<string> { "Acetylation", "Ammonia loss", "Carbamyl", "Deamidation", "Formylation",
                "N2-acetylarginine", "N6-acetyllysine", "N-acetylalanine", "N-acetylaspartate", "N-acetylcysteine", "N-acetylglutamate", "N-acetylglycine",
                "N-acetylisoleucine", "N-acetylmethionine", "N-acetylproline", "N-acetylserine", "N-acetylthreonine", "N-acetyltyrosine", "N-acetylvaline",
                "Phosphorylation", "Phosphoserine", "Phosphothreonine", "Phosphotyrosine", "Sulfonation" };

            return modifications.Select(n => n.OriginalId).Intersect(shiftingModifications).Count();
        }
        private void Warn(string v)
        {
            DigestionWarnHandler?.Invoke(null, new StringEventArgs(v, null));
        }

        public override MyTaskResults RunSpecific(MyTaskResults digestionResults, List<string> peptideFilePaths)
        {
            throw new NotImplementedException();
        }

        public static List<List<string>> SplitPeptides(List<string> allPeptides, int size)
        {
            List<List<string>> allResultsSplit = new List<List<string>>();
            for (int i = 0; i < allPeptides.Count; i += size)
            {
                allResultsSplit.Add(allPeptides.GetRange(i, Math.Min(size, allPeptides.Count - i)));
            }
            return allResultsSplit;
        }

        protected static void WritePeptidesToTsv(Dictionary<string, Dictionary<string, Dictionary<Protein, List<InSilicoPep>>>> peptideByFile, string filePath, Parameters userParams)
        {
            string tab = "\t";
            string header = "Database" + tab + "Protease" + tab + "Base Sequence" + tab + "Full Sequence" + tab + "Previous Amino Acid" + tab +
                "Next Amino Acid" + tab + "Length" + tab + "Molecular Weight" + tab + "Protein" + tab + "Unique (in database)" + tab + "Unique (in analysis)" +
                tab + "Hydrophobicity" + tab + "Electrophoretic Mobility";
            List<InSilicoPep> allPeptides = new List<InSilicoPep>();
            if (peptideByFile.Count > 1)
            {
                Dictionary<string, List<InSilicoPep>> allDatabasePeptidesByProtease = new Dictionary<string, List<InSilicoPep>>();
                foreach (var database in peptideByFile)
                {
                    foreach (var protease in database.Value)
                    {
                        if (allDatabasePeptidesByProtease.ContainsKey(protease.Key))
                        {
                            foreach (var protein in protease.Value)
                            {
                                allDatabasePeptidesByProtease[protease.Key].AddRange(protein.Value);
                            }
                        }
                        else
                        {
                            allDatabasePeptidesByProtease.Add(protease.Key, protease.Value.SelectMany(p => p.Value).ToList());
                        }
                    }
                }
                foreach (var protease in allDatabasePeptidesByProtease)
                {
                    Dictionary<string, List<InSilicoPep>> peptidesToProteins = new Dictionary<string, List<InSilicoPep>>();
                    if (userParams.TreatModifiedPeptidesAsDifferent)
                    {
                        peptidesToProteins = protease.Value.GroupBy(p => p.FullSequence).ToDictionary(group => group.Key, group => group.ToList());
                    }
                    else
                    {
                        peptidesToProteins = protease.Value.GroupBy(p => p.BaseSequence).ToDictionary(group => group.Key, group => group.ToList());
                    }
                    var unique = peptidesToProteins.Where(p => p.Value.Select(p => p.Protein).Distinct().Count() == 1).ToList();
                    var shared = peptidesToProteins.Where(p => p.Value.Select(p => p.Protein).Distinct().Count() > 1).ToList();
                    foreach (var entry in unique)
                    {
                        foreach (var peptide in entry.Value)
                        {
                            peptide.UniqueAllDbs = true;
                            allPeptides.Add(peptide);
                        }
                    }
                    foreach (var entry in shared)
                    {
                        foreach (var peptide in entry.Value)
                        {
                            peptide.UniqueAllDbs = false;
                            allPeptides.Add(peptide);
                        }
                    }

                }
            }
            else
            {
                foreach (var database in peptideByFile)
                {
                    foreach (var protease in database.Value)
                    {
                        foreach (var protein in protease.Value)
                        {
                            foreach (var peptide in protein.Value)
                            {
                                peptide.UniqueAllDbs = peptide.Unique;
                                allPeptides.Add(peptide);
                            }
                        }
                    }
                }
            }
            var numberOfPeptides = allPeptides.Count();
            double numberOfFiles = Math.Ceiling(numberOfPeptides / 1000000.0);
            var peptidesInFile = 1;
            var peptideIndex = 0;
            var fileCount = 1;           

                while (fileCount <= Convert.ToInt32(numberOfFiles))
                {
                    using (StreamWriter output = new StreamWriter(filePath + @"\ProteaseGuruPeptides_" + fileCount + ".tsv"))
                    {
                        output.WriteLine(header);
                        while (peptidesInFile < 1000000)
                        {
                            if (peptideIndex < numberOfPeptides)
                            {
                                output.WriteLine(allPeptides[peptideIndex].ToString());
                                peptideIndex++;
                            }                            
                            peptidesInFile++;
                                                        
                        }
                        output.Close();
                        peptidesInFile = 1;
                    }                    
                    fileCount++;
                }   
        }
    }
}
