﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BurnOutSharp;
using MPF.Core.Data;
using MPF.Modules;
using Newtonsoft.Json;
using RedumpLib.Data;
using RedumpLib.Web;

namespace MPF.Library
{
    public static class InfoTool
    {
        #region Information Extraction

        /// <summary>
        /// Extract all of the possible information from a given input combination
        /// </summary>
        /// <param name="outputDirectory">Output folder to write to</param>
        /// <param name="outputFilename">Output filename to use as the base path</param>
        /// <param name="drive">Drive object representing the current drive</param>
        /// <param name="system">Currently selected system</param>
        /// <param name="mediaType">Currently selected media type</param>
        /// <param name="options">Options object representing user-defined options</param>
        /// <param name="parameters">Parameters object representing what to send to the internal program</param>
        /// <param name="resultProgress">Optional result progress callback</param>
        /// <param name="protectionProgress">Optional protection progress callback</param>
        /// <returns>SubmissionInfo populated based on outputs, null on error</returns>
        public static async Task<SubmissionInfo> ExtractOutputInformation(
            string outputDirectory,
            string outputFilename,
            Drive drive,
            RedumpSystem? system,
            MediaType? mediaType,
            Options options,
            BaseParameters parameters,
            IProgress<Result> resultProgress = null,
            IProgress<ProtectionProgress> protectionProgress = null)
        {
            // Ensure the current disc combination should exist
            if (!system.MediaTypes().Contains(mediaType))
                return null;

            // Sanitize the output filename to strip off any potential extension
            outputFilename = Path.GetFileNameWithoutExtension(outputFilename);

            // Check that all of the relevant files are there
            (bool foundFiles, List<string> missingFiles) = FoundAllFiles(outputDirectory, outputFilename, parameters, false);
            if (!foundFiles)
            {
                resultProgress.Report(Result.Failure($"There were files missing from the output:\n{string.Join("\n", missingFiles)}"));
                return null;
            }

            // Create the SubmissionInfo object with all user-inputted values by default
            string combinedBase = Path.Combine(outputDirectory, outputFilename);
            SubmissionInfo info = new SubmissionInfo()
            {
                CommonDiscInfo = new CommonDiscInfoSection()
                {
                    System = system,
                    Media = mediaType.ToDiscType(),
                    Title = (options.AddPlaceholders ? Template.RequiredValue : ""),
                    ForeignTitleNonLatin = (options.AddPlaceholders ? Template.OptionalValue : ""),
                    DiscNumberLetter = (options.AddPlaceholders ? Template.OptionalValue : ""),
                    DiscTitle = (options.AddPlaceholders ? Template.OptionalValue : ""),
                    Category = null,
                    Region = null,
                    Languages = null,
                    Serial = (options.AddPlaceholders ? Template.RequiredIfExistsValue : ""),
                    Barcode = (options.AddPlaceholders ? Template.OptionalValue : ""),
                    Contents = (options.AddPlaceholders ? Template.OptionalValue : ""),
                },
                VersionAndEditions = new VersionAndEditionsSection()
                {
                    Version = (options.AddPlaceholders ? Template.RequiredIfExistsValue : ""),
                    OtherEditions = (options.AddPlaceholders ? "Original (VERIFY THIS)" : ""),
                },
                TracksAndWriteOffsets = new TracksAndWriteOffsetsSection(),
            };

            // Get specific tool output handling
            parameters.GenerateSubmissionInfo(info, combinedBase, drive, options.IncludeArtifacts);

            // Get a list of matching IDs for each line in the DAT
            if (!string.IsNullOrEmpty(info.TracksAndWriteOffsets.ClrMameProData) && options.HasRedumpLogin)
            {
                // Set the current dumper based on username
                info.DumpersAndStatus.Dumpers = new string[] { options.RedumpUsername };

                info.MatchedIDs = new List<int>();
                using (RedumpWebClient wc = new RedumpWebClient())
                {
                    // Login to Redump
                    bool? loggedIn = wc.Login(options.RedumpUsername, options.RedumpPassword);
                    if (loggedIn == null)
                    {
                        resultProgress?.Report(Result.Failure("There was an unknown error connecting to Redump"));
                    }
                    else if (loggedIn == true)
                    {
                        // Loop through all of the hashdata to find matching IDs
                        resultProgress?.Report(Result.Success("Finding disc matches on Redump..."));
                        string[] splitData = info.TracksAndWriteOffsets.ClrMameProData.Split('\n');
                        foreach (string hashData in splitData)
                        {
                            if (GetISOHashValues(hashData, out long _, out string _, out string _, out string sha1))
                            {
                                // Get all matching IDs for the track
                                List<int> newIds = ListSearchResults(wc, sha1);

                                // If we got null back, there was an error
                                if (newIds == null)
                                {
                                    resultProgress?.Report(Result.Failure("There was an unknown error retrieving information from Redump"));
                                    break;
                                }

                                // If no IDs match any track, then we don't match a disc at all
                                if (!newIds.Any())
                                {
                                    info.MatchedIDs = new List<int>();
                                    break;
                                }

                                // If we have multiple tracks, only take IDs that are in common
                                if (info.MatchedIDs.Any())
                                    info.MatchedIDs = info.MatchedIDs.Intersect(newIds).ToList();

                                // If we're on the first track, all IDs are added
                                else
                                    info.MatchedIDs = newIds;
                            }
                        }

                        resultProgress?.Report(Result.Success("Match finding complete! " + (info.MatchedIDs.Count > 0 ? "Matched IDs: " + string.Join(",", info.MatchedIDs) : "No matches found")));

                        // If we have exactly 1 ID, we can grab a bunch of info from it
                        if (info.MatchedIDs.Count == 1)
                        {
                            resultProgress?.Report(Result.Success($"Filling fields from existing ID {info.MatchedIDs[0]}..."));
                            FillFromId(wc, info, info.MatchedIDs[0]);
                            resultProgress?.Report(Result.Success("Information filling complete!"));
                        }
                    }
                }
            }

            // If we have both ClrMamePro and Size and Checksums data, remove the ClrMamePro
            if (!string.IsNullOrWhiteSpace(info.SizeAndChecksums.CRC32))
                info.TracksAndWriteOffsets.ClrMameProData = null;

            // Extract info based generically on MediaType
            switch (mediaType)
            {
                case MediaType.CDROM:
                case MediaType.GDROM: // TODO: Verify GD-ROM outputs this
                    info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer1MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0AdditionalMould = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    break;

                case MediaType.DVD:
                case MediaType.HDDVD:
                case MediaType.BluRay:
                    // If we have a single-layer disc
                    if (info.SizeAndChecksums.Layerbreak == default)
                    {
                        info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0AdditionalMould = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    }
                    // If we have a dual-layer disc
                    else
                    {
                        info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0AdditionalMould = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");

                        info.CommonDiscInfo.Layer1MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    }

                    break;

                case MediaType.NintendoGameCubeGameDisc:
                    info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer1MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.CommonDiscInfo.Layer0AdditionalMould = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    info.Extras.BCA = info.Extras.BCA ?? (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case MediaType.NintendoWiiOpticalDisc:
                    // If we have a single-layer disc
                    if (info.SizeAndChecksums.Layerbreak == default)
                    {
                        info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0AdditionalMould = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    }
                    // If we have a dual-layer disc
                    else
                    {
                        info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0AdditionalMould = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");

                        info.CommonDiscInfo.Layer1MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    }

                    info.Extras.DiscKey = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.Extras.BCA = info.Extras.BCA ?? (options.AddPlaceholders ? Template.RequiredValue : "");

                    break;

                case MediaType.UMD:
                    // If we have a single-layer disc
                    if (info.SizeAndChecksums.Layerbreak == default)
                    {
                        info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    }
                    // If we have a dual-layer disc
                    else
                    {
                        info.CommonDiscInfo.Layer0MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer0MouldSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");

                        info.CommonDiscInfo.Layer1MasteringRing = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1MasteringSID = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                        info.CommonDiscInfo.Layer1ToolstampMasteringCode = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    }

                    info.SizeAndChecksums.CRC32 = info.SizeAndChecksums.CRC32 ?? (options.AddPlaceholders ? Template.RequiredValue + " [Not automatically generated for UMD]" : "");
                    info.SizeAndChecksums.MD5 = info.SizeAndChecksums.MD5 ?? (options.AddPlaceholders ? Template.RequiredValue + " [Not automatically generated for UMD]" : "");
                    info.SizeAndChecksums.SHA1 = info.SizeAndChecksums.SHA1 ?? (options.AddPlaceholders ? Template.RequiredValue + " [Not automatically generated for UMD]" : "");
                    info.TracksAndWriteOffsets.ClrMameProData = null;
                    break;
            }

            // Extract info based specifically on RedumpSystem
            switch (system)
            {
                case RedumpSystem.AcornArchimedes:
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.UK;
                    break;

                case RedumpSystem.AppleMacintosh:
                case RedumpSystem.EnhancedCD:
                case RedumpSystem.IBMPCcompatible:
                case RedumpSystem.PalmOS:
                case RedumpSystem.PocketPC:
                case RedumpSystem.RainbowDisc:
                    if (string.IsNullOrWhiteSpace(info.CommonDiscInfo.Comments))
                        info.CommonDiscInfo.Comments += $"[T:ISBN] {(options.AddPlaceholders ? Template.OptionalValue : "")}";

                    resultProgress?.Report(Result.Success("Running copy protection scan... this might take a while!"));
                    info.CopyProtection.Protection = await GetCopyProtection(drive, options, protectionProgress);
                    resultProgress?.Report(Result.Success("Copy protection scan complete!"));

                    break;

                case RedumpSystem.AudioCD:
                case RedumpSystem.DVDAudio:
                case RedumpSystem.SuperAudioCD:
                    info.CommonDiscInfo.Category = info.CommonDiscInfo.Category ?? DiscCategory.Audio;
                    break;

                case RedumpSystem.BandaiPlaydiaQuickInteractiveSystem:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.BDVideo:
                    info.CommonDiscInfo.Category = info.CommonDiscInfo.Category ?? DiscCategory.BonusDiscs;
                    info.CopyProtection.Protection = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    break;

                case RedumpSystem.CommodoreAmigaCD:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.CommodoreAmigaCD32:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Europe;
                    break;

                case RedumpSystem.CommodoreAmigaCDTV:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Europe;
                    break;

                case RedumpSystem.DVDVideo:
                    info.CommonDiscInfo.Category = info.CommonDiscInfo.Category ?? DiscCategory.BonusDiscs;
                    break;

                case RedumpSystem.FujitsuFMTownsseries:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.FujitsuFMTownsMarty:
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.IncredibleTechnologiesEagle:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.KonamieAmusement:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.KonamiFireBeat:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.KonamiSystemGV:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.KonamiSystem573:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.KonamiTwinkle:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.MattelHyperScan:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.NamcoSegaNintendoTriforce:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.NavisoftNaviken21:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.NECPC88series:
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.NECPC98series:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.NECPCFXPCFXGA:
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.SegaChihiro:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.SegaDreamcast:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.SegaNaomi:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.SegaNaomi2:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.SegaTitanVideo:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.SharpX68000:
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.SNKNeoGeoCD:
                    info.CommonDiscInfo.EXEDateBuildDate = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.SonyPlayStation:
                    if (drive != null)
                    {
                        resultProgress?.Report(Result.Success("Checking for anti-modchip strings... this might take a while!"));
                        info.CopyProtection.AntiModchip = await GetAntiModchipDetected(drive) ? YesNo.Yes : YesNo.No;
                        resultProgress?.Report(Result.Success("Anti-modchip string scan complete!"));
                    }

                    // Special case for DIC only
                    if (parameters.InternalProgram == InternalProgram.DiscImageCreator)
                    {
                        resultProgress?.Report(Result.Success("Checking for LibCrypt status... this might take a while!"));
                        GetLibCryptDetected(info, combinedBase);
                        resultProgress?.Report(Result.Success("LibCrypt status checking complete!"));
                    }

                    break;

                case RedumpSystem.SonyPlayStation2:
                    info.CommonDiscInfo.LanguageSelection = new LanguageSelection?[] { LanguageSelection.BiosSettings, LanguageSelection.LanguageSelector, LanguageSelection.OptionsMenu };
                    break;

                case RedumpSystem.SonyPlayStation3:
                    info.Extras.DiscKey = (options.AddPlaceholders ? Template.RequiredValue : "");
                    info.Extras.DiscID = (options.AddPlaceholders ? Template.RequiredValue : "");
                    break;

                case RedumpSystem.TomyKissSite:
                    info.CommonDiscInfo.Region = info.CommonDiscInfo.Region ?? Region.Japan;
                    break;

                case RedumpSystem.ZAPiTGamesGameWaveFamilyEntertainmentSystem:
                    info.CopyProtection.Protection = (options.AddPlaceholders ? Template.RequiredIfExistsValue : "");
                    break;
            }

            // Set the category if it's not overriden
            info.CommonDiscInfo.Category = info.CommonDiscInfo.Category ?? DiscCategory.Games;

            // Comments is one of the few fields with odd handling
            if (string.IsNullOrEmpty(info.CommonDiscInfo.Comments))
                info.CommonDiscInfo.Comments = (options.AddPlaceholders ? Template.OptionalValue : "");

            return info;
        }

        /// <summary>
        /// Ensures that all required output files have been created
        /// </summary>
        /// <param name="outputDirectory">Output folder to write to</param>
        /// <param name="outputFilename">Output filename to use as the base path</param>
        /// <param name="parameters">Parameters object representing what to send to the internal program</param>
        /// <param name="preCheck">True if this is a check done before a dump, false if done after</param>
        /// <returns>Tuple of true if all required files exist, false otherwise and a list representing missing files</returns>
        public static (bool, List<string>) FoundAllFiles(string outputDirectory, string outputFilename, BaseParameters parameters, bool preCheck)
        {
            // First, sanitized the output filename to strip off any potential extension
            outputFilename = Path.GetFileNameWithoutExtension(outputFilename);

            // Then get the base path for all checking
            string basePath = Path.Combine(outputDirectory, outputFilename);

            // Finally, let the parameters say if all files exist
            return parameters.CheckAllOutputFilesExist(basePath, preCheck);
        }

        /// <summary>
        /// Get the existance of an anti-modchip string from a PlayStation disc, if possible
        /// </summary>
        /// <param name="drive">Drive object representing the current drive</param>
        /// <returns>Anti-modchip existence if possible, false on error</returns>
        private static async Task<bool> GetAntiModchipDetected(Drive drive)
            => await Protection.GetPlayStationAntiModchipDetected($"{drive.Letter}:\\");

        /// <summary>
        /// Get the current copy protection scheme, if possible
        /// </summary>
        /// <param name="drive">Drive object representing the current drive</param>
        /// <param name="options">Options object that determines what to scan</param>
        /// <param name="progress">Optional progress callback</param>
        /// <returns>Copy protection scheme if possible, null on error</returns>
        private static async Task<string> GetCopyProtection(Drive drive, Options options, IProgress<ProtectionProgress> progress = null)
        {
            if (options.ScanForProtection && drive != null)
            {
                (bool success, string output) = await Protection.RunProtectionScanOnPath($"{drive.Letter}:\\", options, progress);
                if (success)
                    return output;
                else
                    return "An error occurred while scanning!";
            }

            return "(CHECK WITH PROTECTIONID)";
        }

        /// <summary>
        /// Get the full lines from the input file, if possible
        /// </summary>
        /// <param name="filename">file location</param>
        /// <param name="binary">True if should read as binary, false otherwise (default)</param>
        /// <returns>Full text of the file, null on error</returns>
        private static string GetFullFile(string filename, bool binary = false)
        {
            // If the file doesn't exist, we can't get info from it
            if (!File.Exists(filename))
                return null;

            // If we're reading as binary
            if (binary)
            {
                byte[] bytes = File.ReadAllBytes(filename);
                return BitConverter.ToString(bytes).Replace("-", string.Empty);
            }

            return string.Join("\n", File.ReadAllLines(filename));
        }

        /// <summary>
        /// Get the split values for ISO-based media
        /// </summary>
        /// <param name="hashData">String representing the combined hash data</param>
        /// <returns>True if extraction was successful, false otherwise</returns>
        private static bool GetISOHashValues(string hashData, out long size, out string crc32, out string md5, out string sha1)
        {
            size = -1; crc32 = null; md5 = null; sha1 = null;

            if (string.IsNullOrWhiteSpace(hashData))
                return false;

            Regex hashreg = new Regex(@"<rom name="".*?"" size=""(.*?)"" crc=""(.*?)"" md5=""(.*?)"" sha1=""(.*?)""");
            Match m = hashreg.Match(hashData);
            if (m.Success)
            {
                Int64.TryParse(m.Groups[1].Value, out size);
                crc32 = m.Groups[2].Value;
                md5 = m.Groups[3].Value;
                sha1 = m.Groups[4].Value;
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Get if LibCrypt data is detected in the subchannel file, if possible
        /// </summary>
        /// <param name="info">Base submission info to fill in specifics for</param>
        /// <param name="basePath">Base filename and path to use for checking</param>
        /// <returns>Status of the LibCrypt data, if possible</returns>
        private static void GetLibCryptDetected(SubmissionInfo info, string basePath)
        {
            bool? psLibCryptStatus = Protection.GetLibCryptDetected(basePath + ".sub");
            if (psLibCryptStatus == true)
            {
                // Guard against false positives
                if (File.Exists(basePath + "_subIntention.txt"))
                {
                    string libCryptData = GetFullFile(basePath + "_subIntention.txt") ?? "";
                    if (string.IsNullOrEmpty(libCryptData))
                    {
                        info.CopyProtection.LibCrypt = YesNo.No;
                    }
                    else
                    {
                        info.CopyProtection.LibCrypt = YesNo.Yes;
                        info.CopyProtection.LibCryptData = libCryptData;
                    }
                }
                else
                {
                    info.CopyProtection.LibCrypt = YesNo.No;
                }
            }
            else if (psLibCryptStatus == false)
            {
                info.CopyProtection.LibCrypt = YesNo.No;
            }
            else
            {
                info.CopyProtection.LibCrypt = YesNo.NULL;
                info.CopyProtection.LibCryptData = "LibCrypt could not be detected because subchannel file is missing";
            }
        }

        #endregion

        #region Information Output

        /// <summary>
        /// Compress log files to save space
        /// </summary>
        /// <param name="outputDirectory">Output folder to write to</param>
        /// <param name="outputFilename">Output filename to use as the base path</param>
        /// <param name="parameters">Parameters object to use to derive log file paths</param>
        /// <returns>True if the process succeeded, false otherwise</returns>
        public static bool CompressLogFiles(string outputDirectory, string outputFilename, BaseParameters parameters)
        {
            // Prepare the necessary paths
            outputFilename = Path.GetFileNameWithoutExtension(outputFilename);
            string combinedBase = Path.Combine(outputDirectory, outputFilename);
            string archiveName = combinedBase + "_logs.zip";

            // Get the list of log files from the parameters object
            var files = parameters.GetLogFilePaths(combinedBase);
            if (!files.Any())
                return true;

            // Add the log files to the archive and delete the uncompressed file after
            ZipArchive zf = null;
            try
            {
                zf = ZipFile.Open(archiveName, ZipArchiveMode.Create);
                foreach (string file in files)
                {
                    string entryName = file.Substring(outputDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    zf.CreateEntryFromFile(file, entryName);

                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                zf?.Dispose();
            }
        }

        /// <summary>
        /// Format the output data in a human readable way, separating each printed line into a new item in the list
        /// </summary>
        /// <param name="info">Information object that should contain normalized values</param>
        /// <returns>List of strings representing each line of an output file, null on error</returns>
        public static List<string> FormatOutputData(SubmissionInfo info)
        {
            // Check to see if the inputs are valid
            if (info == null)
                return null;

            try
            {
                // Sony-printed discs have layers in the opposite order
                var system = info.CommonDiscInfo.System;
                bool reverseOrder = (system == RedumpSystem.SonyPlayStation2
                    || system == RedumpSystem.SonyPlayStation3
                    || system == RedumpSystem.SonyPlayStation4);

                // Common Disc Info section
                List<string> output = new List<string> { "Common Disc Info:" };
                AddIfExists(output, Template.TitleField, info.CommonDiscInfo.Title, 1);
                AddIfExists(output, Template.ForeignTitleField, info.CommonDiscInfo.ForeignTitleNonLatin, 1);
                AddIfExists(output, Template.DiscNumberField, info.CommonDiscInfo.DiscNumberLetter, 1);
                AddIfExists(output, Template.DiscTitleField, info.CommonDiscInfo.DiscTitle, 1);
                AddIfExists(output, Template.SystemField, info.CommonDiscInfo.System.LongName(), 1);
                AddIfExists(output, Template.MediaTypeField, GetFixedMediaType(
                        info.CommonDiscInfo.Media.ToMediaType(),
                        info.SizeAndChecksums.Size,
                        info.SizeAndChecksums.Layerbreak,
                        info.SizeAndChecksums.Layerbreak2,
                        info.SizeAndChecksums.Layerbreak3),
                    1);
                AddIfExists(output, Template.CategoryField, info.CommonDiscInfo.Category.LongName(), 1);
                AddIfExists(output, Template.MatchingIDsField, info.MatchedIDs, 1);
                AddIfExists(output, Template.RegionField, info.CommonDiscInfo.Region.LongName() ?? "SPACE! (CHANGE THIS)", 1);
                AddIfExists(output, Template.LanguagesField, (info.CommonDiscInfo.Languages ?? new Language?[] { null }).Select(l => l.LongName() ?? "Klingon (CHANGE THIS)").ToArray(), 1);
                AddIfExists(output, Template.PlaystationLanguageSelectionViaField, (info.CommonDiscInfo.LanguageSelection ?? new LanguageSelection?[] { }).Select(l => l.LongName()).ToArray(), 1);
                AddIfExists(output, Template.DiscSerialField, info.CommonDiscInfo.Serial, 1);

                // All ringcode information goes in an indented area
                output.Add(""); output.Add("\tRingcode Information:");

                // If we have a triple-layer disc
                if (info.SizeAndChecksums.Layerbreak3 != default)
                {
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.MasteringRingField, info.CommonDiscInfo.Layer0MasteringRing, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.MasteringSIDField, info.CommonDiscInfo.Layer0MasteringSID, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.ToolstampField, info.CommonDiscInfo.Layer0ToolstampMasteringCode, 2);
                    AddIfExists(output, "Data Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer0MouldSID, 2);
                    AddIfExists(output, "Data Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer0AdditionalMould, 2);

                    AddIfExists(output, "Layer 1 " + Template.MasteringRingField, info.CommonDiscInfo.Layer1MasteringRing, 2);
                    AddIfExists(output, "Layer 1 " + Template.MasteringSIDField, info.CommonDiscInfo.Layer1MasteringSID, 2);
                    AddIfExists(output, "Layer 1 " + Template.ToolstampField, info.CommonDiscInfo.Layer1ToolstampMasteringCode, 2);
                    AddIfExists(output, "Label Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer1MouldSID, 2);
                    AddIfExists(output, "Label Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer1AdditionalMould, 2);

                    AddIfExists(output, "Layer 2 " + Template.MasteringRingField, info.CommonDiscInfo.Layer2MasteringRing, 2);
                    AddIfExists(output, "Layer 2 " + Template.MasteringSIDField, info.CommonDiscInfo.Layer2MasteringSID, 2);
                    AddIfExists(output, "Layer 2 " + Template.ToolstampField, info.CommonDiscInfo.Layer2ToolstampMasteringCode, 2);

                    AddIfExists(output, (reverseOrder ? "Layer 3 (Inner) " : "Layer 3 (Outer) ") + Template.MasteringRingField, info.CommonDiscInfo.Layer3MasteringRing, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 3 (Inner) " : "Layer 3 (Outer) ") + Template.MasteringSIDField, info.CommonDiscInfo.Layer3MasteringSID, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 3 (Inner) " : "Layer 3 (Outer) ") + Template.ToolstampField, info.CommonDiscInfo.Layer3ToolstampMasteringCode, 2);
                }
                // If we have a triple-layer disc
                else if (info.SizeAndChecksums.Layerbreak2 != default)
                {
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.MasteringRingField, info.CommonDiscInfo.Layer0MasteringRing, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.MasteringSIDField, info.CommonDiscInfo.Layer0MasteringSID, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.ToolstampField, info.CommonDiscInfo.Layer0ToolstampMasteringCode, 2);
                    AddIfExists(output, "Data Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer0MouldSID, 2);
                    AddIfExists(output, "Data Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer0AdditionalMould, 2);

                    AddIfExists(output, "Layer 1 " + Template.MasteringRingField, info.CommonDiscInfo.Layer1MasteringRing, 2);
                    AddIfExists(output, "Layer 1 " + Template.MasteringSIDField, info.CommonDiscInfo.Layer1MasteringSID, 2);
                    AddIfExists(output, "Layer 1 " + Template.ToolstampField, info.CommonDiscInfo.Layer1ToolstampMasteringCode, 2);
                    AddIfExists(output, "Label Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer1MouldSID, 2);
                    AddIfExists(output, "Label Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer1AdditionalMould, 2);

                    AddIfExists(output, (reverseOrder ? "Layer 2 (Inner) " : "Layer 2 (Outer) ") + Template.MasteringRingField, info.CommonDiscInfo.Layer2MasteringRing, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 2 (Inner) " : "Layer 2 (Outer) ") + Template.MasteringSIDField, info.CommonDiscInfo.Layer2MasteringSID, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 2 (Inner) " : "Layer 2 (Outer) ") + Template.ToolstampField, info.CommonDiscInfo.Layer2ToolstampMasteringCode, 2);
                }
                // If we have a dual-layer disc
                else if (info.SizeAndChecksums.Layerbreak != default)
                {
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.MasteringRingField, info.CommonDiscInfo.Layer0MasteringRing, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.MasteringSIDField, info.CommonDiscInfo.Layer0MasteringSID, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 0 (Outer) " : "Layer 0 (Inner) ") + Template.ToolstampField, info.CommonDiscInfo.Layer0ToolstampMasteringCode, 2);
                    AddIfExists(output, "Data Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer0MouldSID, 2);
                    AddIfExists(output, "Data Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer0AdditionalMould, 2);

                    AddIfExists(output, (reverseOrder ? "Layer 1 (Inner) " : "Layer 1 (Outer) ") + Template.MasteringRingField, info.CommonDiscInfo.Layer1MasteringRing, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 1 (Inner) " : "Layer 1 (Outer) ") + Template.MasteringSIDField, info.CommonDiscInfo.Layer1MasteringSID, 2);
                    AddIfExists(output, (reverseOrder ? "Layer 1 (Inner) " : "Layer 1 (Outer) ") + Template.ToolstampField, info.CommonDiscInfo.Layer1ToolstampMasteringCode, 2);
                    AddIfExists(output, "Label Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer1MouldSID, 2);
                    AddIfExists(output, "Label Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer1AdditionalMould, 2);
                }
                // If we have a single-layer disc
                else
                {
                    AddIfExists(output, "Data Side " + Template.MasteringRingField, info.CommonDiscInfo.Layer0MasteringRing, 2);
                    AddIfExists(output, "Data Side " + Template.MasteringSIDField, info.CommonDiscInfo.Layer0MasteringSID, 2);
                    AddIfExists(output, "Data Side " + Template.ToolstampField, info.CommonDiscInfo.Layer0ToolstampMasteringCode, 2);
                    AddIfExists(output, "Data Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer0MouldSID, 2);
                    AddIfExists(output, "Data Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer0AdditionalMould, 2);

                    AddIfExists(output, "Label Side " + Template.MasteringRingField, info.CommonDiscInfo.Layer1MasteringRing, 2);
                    AddIfExists(output, "Label Side " + Template.MasteringSIDField, info.CommonDiscInfo.Layer1MasteringSID, 2);
                    AddIfExists(output, "Label Side " + Template.ToolstampField, info.CommonDiscInfo.Layer1ToolstampMasteringCode, 2);
                    AddIfExists(output, "Label Side " + Template.MouldSIDField, info.CommonDiscInfo.Layer1MouldSID, 2);
                    AddIfExists(output, "Label Side " + Template.AdditionalMouldField, info.CommonDiscInfo.Layer1AdditionalMould, 2);
                }

                AddIfExists(output, Template.BarcodeField, info.CommonDiscInfo.Barcode, 1);
                AddIfExists(output, Template.EXEDateBuildDate, info.CommonDiscInfo.EXEDateBuildDate, 1);
                AddIfExists(output, Template.ErrorCountField, info.CommonDiscInfo.ErrorsCount, 1);
                AddIfExists(output, Template.CommentsField, info.CommonDiscInfo.Comments.Trim(), 1);
                AddIfExists(output, Template.ContentsField, info.CommonDiscInfo.Contents.Trim(), 1);

                // Version and Editions section
                output.Add(""); output.Add("Version and Editions:");
                AddIfExists(output, Template.VersionField, info.VersionAndEditions.Version, 1);
                AddIfExists(output, Template.EditionField, info.VersionAndEditions.OtherEditions, 1);

                // EDC section
                if (info.CommonDiscInfo.System == RedumpSystem.SonyPlayStation)
                {
                    output.Add(""); output.Add("EDC:");
                    AddIfExists(output, Template.PlayStationEDCField, info.EDC.EDC.LongName(), 1);
                }

                // Parent/Clone Relationship section
                // output.Add(""); output.Add("Parent/Clone Relationship:");
                // AddIfExists(output, Template.ParentIDField, info.ParentID);
                // AddIfExists(output, Template.RegionalParentField, info.RegionalParent.ToString());

                // Extras section
                if (info.Extras.PVD != null || info.Extras.PIC != null || info.Extras.BCA != null)
                {
                    output.Add(""); output.Add("Extras:");
                    AddIfExists(output, Template.PVDField, info.Extras.PVD?.Trim(), 1);
                    AddIfExists(output, Template.PlayStation3WiiDiscKeyField, info.Extras.DiscKey, 1);
                    AddIfExists(output, Template.PlayStation3DiscIDField, info.Extras.DiscID, 1);
                    AddIfExists(output, Template.PICField, info.Extras.PIC, 1);
                    AddIfExists(output, Template.HeaderField, info.Extras.Header, 1);
                    AddIfExists(output, Template.GameCubeWiiBCAField, info.Extras.BCA, 1);
                    AddIfExists(output, Template.XBOXSSRanges, info.Extras.SecuritySectorRanges, 1);
                }

                // Copy Protection section
                if (info.CopyProtection.Protection != null
                    || info.CopyProtection.AntiModchip != YesNo.NULL
                    || info.CopyProtection.LibCrypt != YesNo.NULL)
                {
                    output.Add(""); output.Add("Copy Protection:");
                    if (info.CommonDiscInfo.System == RedumpSystem.SonyPlayStation)
                    {
                        AddIfExists(output, Template.PlayStationAntiModchipField, info.CopyProtection.AntiModchip.LongName(), 1);
                        AddIfExists(output, Template.PlayStationLibCryptField, info.CopyProtection.LibCrypt.LongName(), 1);
                        AddIfExists(output, Template.SubIntentionField, info.CopyProtection.LibCryptData, 1);
                    }

                    AddIfExists(output, Template.CopyProtectionField, info.CopyProtection.Protection, 1);
                    AddIfExists(output, Template.SubIntentionField, info.CopyProtection.SecuROMData, 1);
                }

                // Dumpers and Status section
                // output.Add(""); output.Add("Dumpers and Status");
                // AddIfExists(output, Template.StatusField, info.Status.Name());
                // AddIfExists(output, Template.OtherDumpersField, info.OtherDumpers);

                // Tracks and Write Offsets section
                if (!string.IsNullOrWhiteSpace(info.TracksAndWriteOffsets.ClrMameProData))
                {
                    output.Add(""); output.Add("Tracks and Write Offsets:");
                    AddIfExists(output, Template.DATField, info.TracksAndWriteOffsets.ClrMameProData + "\n", 1);
                    AddIfExists(output, Template.CuesheetField, info.TracksAndWriteOffsets.Cuesheet, 1);
                    AddIfExists(output, Template.WriteOffsetField, info.TracksAndWriteOffsets.OtherWriteOffsets, 1);
                }
                // Size & Checksum section
                else
                {
                    output.Add(""); output.Add("Size & Checksum:");
                    AddIfExists(output, Template.LayerbreakField, (info.SizeAndChecksums.Layerbreak == default ? null : info.SizeAndChecksums.Layerbreak.ToString()), 1);
                    AddIfExists(output, Template.SizeField, info.SizeAndChecksums.Size.ToString(), 1);
                    AddIfExists(output, Template.CRC32Field, info.SizeAndChecksums.CRC32, 1);
                    AddIfExists(output, Template.MD5Field, info.SizeAndChecksums.MD5, 1);
                    AddIfExists(output, Template.SHA1Field, info.SizeAndChecksums.SHA1, 1);
                }

                // Make sure there aren't any instances of two blank lines in a row
                string last = null;
                for (int i = 0; i < output.Count;)
                {
                    if (output[i] == last && string.IsNullOrWhiteSpace(last))
                    {
                        output.RemoveAt(i);
                    }
                    else
                    {
                        last = output[i];
                        i++;
                    }
                }

                return output;
            }
            catch
            {
                // We don't care what the error is
                return null;
            }
        }

        /// <summary>
        /// Write the data to the output folder
        /// </summary>
        /// <param name="outputDirectory">Output folder to write to</param>
        /// <param name="lines">Preformatted list of lines to write out to the file</param>
        /// <returns>True on success, false on error</returns>
        public static bool WriteOutputData(string outputDirectory, List<string> lines)
        {
            // Check to see if the inputs are valid
            if (lines == null)
                return false;

            // Now write out to a generic file
            try
            {
                using (StreamWriter sw = new StreamWriter(File.Open(Path.Combine(outputDirectory, "!submissionInfo.txt"), FileMode.Create, FileAccess.Write)))
                {
                    foreach (string line in lines)
                        sw.WriteLine(line);
                }
            }
            catch
            {
                // We don't care what the error is right now
                return false;
            }

            return true;
        }

        /// <summary>
        /// Write the data to the output folder
        /// </summary>
        /// <param name="outputDirectory">Output folder to write to</param>
        /// <param name="info">SubmissionInfo object representing the JSON to write out to the file</param>
        /// <returns>True on success, false on error</returns>
        public static bool WriteOutputData(string outputDirectory, SubmissionInfo info)
        {
            // Check to see if the input is valid
            if (info == null)
                return false;

            // Now write out to the JSON
            try
            {
                using (var fs = File.Create(Path.Combine(outputDirectory, "!submissionInfo.json.gz")))
                using (var gs = new GZipStream(fs, CompressionMode.Compress))
                {
                    string json = JsonConvert.SerializeObject(info, Formatting.Indented);
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                    gs.Write(jsonBytes, 0, jsonBytes.Length);
                }
            }
            catch
            {
                // We don't care what the error is right now
                return false;
            }

            return true;
        }

        /// <summary>
        /// Add the properly formatted key and value, if possible
        /// </summary>
        /// <param name="output">Output list</param>
        /// <param name="key">Name of the output key to write</param>
        /// <param name="value">Name of the output value to write</param>
        /// <param name="indent">Number of tabs to indent the line</param>
        private static void AddIfExists(List<string> output, string key, string value, int indent)
        {
            // If there's no valid value to write
            if (value == null)
                return;

            string prefix = "";
            for (int i = 0; i < indent; i++)
                prefix += "\t";

            // If the value contains a newline
            value = value.Replace("\r\n", "\n");
            if (value.Contains("\n"))
            {
                output.Add(prefix + key + ":"); output.Add("");
                string[] values = value.Split('\n');
                foreach (string val in values)
                    output.Add(val);

                output.Add("");
            }

            // For all regular values
            else
            {
                output.Add(prefix + key + ": " + value);
            }
        }

        /// <summary>
        /// Add the properly formatted key and value, if possible
        /// </summary>
        /// <param name="output">Output list</param>
        /// <param name="key">Name of the output key to write</param>
        /// <param name="value">Name of the output value to write</param>
        /// <param name="indent">Number of tabs to indent the line</param>
        private static void AddIfExists(List<string> output, string key, string[] value, int indent)
        {
            // If there's no valid value to write
            if (value == null || value.Length == 0)
                return;

            AddIfExists(output, key, string.Join(", ", value), indent);
        }

        /// <summary>
        /// Add the properly formatted key and value, if possible
        /// </summary>
        /// <param name="output">Output list</param>
        /// <param name="key">Name of the output key to write</param>
        /// <param name="value">Name of the output value to write</param>
        /// <param name="indent">Number of tabs to indent the line</param>
        private static void AddIfExists(List<string> output, string key, List<int> value, int indent)
        {
            // If there's no valid value to write
            if (value == null || value.Count() == 0)
                return;

            AddIfExists(output, key, string.Join(", ", value.Select(o => o.ToString())), indent);
        }

        /// <summary>
        /// Get the adjusted name of the media baed on layers, if applicable
        /// </summary>
        /// <param name="mediaType">MediaType to get the proper name for</param>
        /// <param name="size">Size of the current media</param>
        /// <param name="layerbreak">First layerbreak value, as applicable</param>
        /// <param name="layerbreak2">Second layerbreak value, as applicable</param>
        /// <param name="layerbreak3">Third ayerbreak value, as applicable</param>
        /// <returns>String representation of the media, including layer specification</returns>
        private static string GetFixedMediaType(MediaType? mediaType, long size, long layerbreak, long layerbreak2, long layerbreak3)
        {
            switch (mediaType)
            {
                case MediaType.DVD:
                    if (layerbreak != default)
                        return $"{mediaType.LongName()}-9";
                    else
                        return $"{mediaType.LongName()}-5";

                case MediaType.BluRay:
                    if (layerbreak3 != default)
                        return $"{mediaType.LongName()}-128";
                    else if (layerbreak2 != default)
                        return $"{mediaType.LongName()}-100";
                    else if (layerbreak != default && size > 53_687_063_712)
                        return $"{mediaType.LongName()}-66";
                    else if (layerbreak != default)
                        return $"{mediaType.LongName()}-50";
                    else if (size > 26_843_531_856)
                        return $"{mediaType.LongName()}-33";
                    else
                        return $"{mediaType.LongName()}-25";

                case MediaType.UMD:
                    if (layerbreak != default)
                        return $"{mediaType.LongName()}-DL";
                    else
                        return $"{mediaType.LongName()}-SL";

                default:
                    return mediaType.LongName();
            }
        }

        #endregion

        #region Normalization

        /// <summary>
        /// Normalize a split set of paths
        /// </summary>
        /// <param name="directory">Directory name to normalize</param>
        /// <param name="filename">Filename to normalize</param>
        /// <param name="replacePeriods">True to replace '.' with '_' in filenames, false otherwise</param>
        public static (string, string) NormalizeOutputPaths(string directory, string filename, bool replacePeriods)
        {
            try
            {
                // Cache if we had a directory separator or not
                bool endedWithDirectorySeparator = directory.EndsWith(Path.DirectorySeparatorChar.ToString())
                    || directory.EndsWith(Path.AltDirectorySeparatorChar.ToString());

                // Combine the path to make things separate easier
                string combinedPath = Path.Combine(directory, filename);

                // If we have have a blank path, just return
                if (string.IsNullOrWhiteSpace(combinedPath))
                    return (directory, filename);

                // Now get the normalized paths
                directory = Path.GetDirectoryName(combinedPath);
                filename = Path.GetFileName(combinedPath);

                // Take care of extra path characters
                directory = new StringBuilder(directory)
                    .Replace(':', '_', 0, directory.LastIndexOf(':') == -1 ? 0 : directory.LastIndexOf(':')).ToString();

                // Sanitize everything else
                foreach (char c in Path.GetInvalidPathChars())
                    directory = directory.Replace(c, '_');
                foreach (char c in Path.GetInvalidFileNameChars())
                    filename = filename.Replace(c, '_');
                if (replacePeriods)
                    filename = Path.GetFileNameWithoutExtension(filename).Replace('.', '_') + "." + Path.GetExtension(filename).TrimStart('.');

                // If we had a directory separator at the end before, add it again
                if (endedWithDirectorySeparator)
                    directory += Path.DirectorySeparatorChar;

                // If we have a root directory, sanitize
                if (Directory.Exists(directory))
                {
                    var possibleRootDir = new DirectoryInfo(directory);
                    if (possibleRootDir.Parent == null)
                        directory = directory.Replace($"{Path.DirectorySeparatorChar}{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}");
                }
            }
            catch { }

            return (directory, filename);
        }

        #endregion

        #region Web Calls

        /// <summary>
        /// Fill out an existing SubmissionInfo object based on a disc page
        /// </summary>
        /// <param name="wc">RedumpWebClient for making the connection</param>
        /// <param name="info">Existing SubmissionInfo object to fill</param>
        /// <param name="id">Redump disc ID to retrieve</param>
        private static void FillFromId(RedumpWebClient wc, SubmissionInfo info, int id)
        {
            string discData = wc.DownloadSingleSiteID(id);
            if (string.IsNullOrEmpty(discData))
                return;

            // Title, Disc Number/Letter, Disc Title
            var match = Constants.TitleRegex.Match(discData);
            if (match.Success)
            {
                string title = WebUtility.HtmlDecode(match.Groups[1].Value);

                // If we have parenthesis, title is everything before the first one
                int firstParenLocation = title.IndexOf(" (");
                if (firstParenLocation >= 0)
                {
                    info.CommonDiscInfo.Title = title.Substring(0, firstParenLocation);
                    var subMatches = Constants.DiscNumberLetterRegex.Match(title);
                    for (int i = 1; i < subMatches.Groups.Count; i++)
                    {
                        string subMatch = subMatches.Groups[i].Value;

                        // Disc number or letter
                        if (subMatch.StartsWith("Disc"))
                            info.CommonDiscInfo.DiscNumberLetter = subMatch.Remove(0, "Disc ".Length);

                        // Disc title
                        else
                            info.CommonDiscInfo.DiscTitle = subMatch;
                    }
                }
                // Otherwise, leave the title as-is
                else
                {
                    info.CommonDiscInfo.Title = title;
                }
            }

            // Foreign Title
            match = Constants.ForeignTitleRegex.Match(discData);
            if (match.Success)
                info.CommonDiscInfo.ForeignTitleNonLatin = WebUtility.HtmlDecode(match.Groups[1].Value);
            else
                info.CommonDiscInfo.ForeignTitleNonLatin = null;

            // Category
            match = Constants.CategoryRegex.Match(discData);
            if (match.Success)
                info.CommonDiscInfo.Category = Extensions.ToDiscCategory(match.Groups[1].Value);
            else
                info.CommonDiscInfo.Category = DiscCategory.Games;

            // Region
            match = Constants.RegionRegex.Match(discData);
            if (match.Success)
                info.CommonDiscInfo.Region = Extensions.ToRegion(match.Groups[1].Value);

            // Languages
            var matches = Constants.LanguagesRegex.Matches(discData);
            if (matches.Count > 0)
            {
                List<Language?> tempLanguages = new List<Language?>();
                foreach (Match submatch in matches)
                    tempLanguages.Add(Extensions.ToLanguage(submatch.Groups[1].Value));

                info.CommonDiscInfo.Languages = tempLanguages.Where(l => l != null).ToArray();
            }

            // Error count
            match = Constants.ErrorCountRegex.Match(discData);
            if (match.Success)
            {
                // If the error count is empty, fill from the page
                if (string.IsNullOrEmpty(info.CommonDiscInfo.ErrorsCount))
                    info.CommonDiscInfo.ErrorsCount = match.Groups[1].Value;
            }

            // Version
            match = Constants.VersionRegex.Match(discData);
            if (match.Success)
                info.VersionAndEditions.Version = WebUtility.HtmlDecode(match.Groups[1].Value);

            // Dumpers
            matches = Constants.DumpersRegex.Matches(discData);
            if (matches.Count > 0)
            {
                // Start with any currently listed dumpers
                List<string> tempDumpers = new List<string>();
                if (info.DumpersAndStatus.Dumpers.Length > 0)
                {
                    foreach (string dumper in info.DumpersAndStatus.Dumpers)
                        tempDumpers.Add(dumper);
                }

                foreach (Match submatch in matches)
                    tempDumpers.Add(WebUtility.HtmlDecode(submatch.Groups[1].Value));

                info.DumpersAndStatus.Dumpers = tempDumpers.ToArray();
            }

            // Comments
            match = Constants.CommentsRegex.Match(discData);
            if (match.Success)
            {
                info.CommonDiscInfo.Comments += (string.IsNullOrEmpty(info.CommonDiscInfo.Comments) ? string.Empty : "\n")
                    + WebUtility.HtmlDecode(match.Groups[1].Value)
                    .Replace("<br />", "\n")
                    .Replace("<b>ISBN</b>", "[T:ISBN]") + "\n";
            }

            // Contents
            match = Constants.ContentsRegex.Match(discData);
            if (match.Success)
            {
                info.CommonDiscInfo.Contents = WebUtility.HtmlDecode(match.Groups[1].Value)
                       .Replace("<br />", "\n")
                       .Replace("</div>", "");
                info.CommonDiscInfo.Contents = Regex.Replace(info.CommonDiscInfo.Contents, @"<div .*?>", "");
            }

            // Added
            match = Constants.AddedRegex.Match(discData);
            if (match.Success)
            {
                if (DateTime.TryParse(match.Groups[1].Value, out DateTime added))
                    info.Added = added;
                else
                    info.Added = null;
            }

            // Last Modified
            match = Constants.LastModifiedRegex.Match(discData);
            if (match.Success)
            {
                if (DateTime.TryParse(match.Groups[1].Value, out DateTime lastModified))
                    info.LastModified = lastModified;
                else
                    info.LastModified = null;
            }
        }

        /// <summary>
        /// List the disc IDs associated with a given quicksearch query
        /// </summary>
        /// <param name="wc">RedumpWebClient for making the connection</param>
        /// <param name="query">Query string to attempt to search for</param>
        /// <returns>All disc IDs for the given query, null on error</returns>
        private static List<int> ListSearchResults(RedumpWebClient wc, string query)
        {
            List<int> ids = new List<int>();

            // Strip quotes
            query = query.Trim('"', '\'');

            // Special characters become dashes
            query = query.Replace(' ', '-');
            query = query.Replace('/', '-');
            query = query.Replace('\\', '/');

            // Lowercase is defined per language
            query = query.ToLowerInvariant();

            // Keep getting quicksearch pages until there are none left
            try
            {
                int pageNumber = 1;
                while (true)
                {
                    List<int> pageIds = wc.CheckSingleSitePage(string.Format(Constants.QuickSearchUrl, query, pageNumber++));
                    ids.AddRange(pageIds);
                    if (pageIds.Count <= 1)
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An exception occurred while trying to log in: {ex}");
                return null;
            }

            return ids;
        }

        #endregion
    }
}
