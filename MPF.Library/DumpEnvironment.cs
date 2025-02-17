﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using BurnOutSharp;
using MPF.Core.Data;
using MPF.Core.Utilities;
using MPF.Modules;
using RedumpLib.Data;

namespace MPF.Library
{
    /// <summary>
    /// Represents the state of all settings to be used during dumping
    /// </summary>
    public class DumpEnvironment
    {
        #region Output paths

        /// <summary>
        /// Base output directory to write files to
        /// </summary>
        public string OutputDirectory { get; private set; }

        /// <summary>
        /// Base output filename for output
        /// </summary>
        public string OutputFilename { get; private set; }

        #endregion

        #region UI information

        /// <summary>
        /// Drive object representing the current drive
        /// </summary>
        public Drive Drive { get; private set; }

        /// <summary>
        /// Currently selected system
        /// </summary>
        public RedumpSystem? System { get; private set; }

        /// <summary>
        /// Currently selected media type
        /// </summary>
        public MediaType? Type { get; private set; }

        /// <summary>
        /// Options object representing user-defined options
        /// </summary>
        public Options Options { get; private set; }

        /// <summary>
        /// Parameters object representing what to send to the internal program
        /// </summary>
        public BaseParameters Parameters { get; private set; }

        #endregion
        
        #region Event Handlers

        /// <summary>
        /// Geneeic way of reporting a message
        /// </summary>
        /// <param name="message">String value to report</param>
        public EventHandler<string> ReportStatus;

        /// <summary>
        /// Queue of items that need to be logged
        /// </summary>
        private ProcessingQueue<string> outputQueue;

        /// <summary>
        /// Event handler for data returned from a process
        /// </summary>
        private void OutputToLog(object proc, string args)
        {
            outputQueue.Enqueue(args);
        }

        /// <summary>
        /// Process the outputs in the queue
        /// </summary>
        private void ProcessOutputs(string nextOutput)
        {
            ReportStatus.Invoke(this, nextOutput);
        }

        #endregion

        /// <summary>
        /// Constructor for a full DumpEnvironment object from user information
        /// </summary>
        /// <param name="options"></param>
        /// <param name="outputDirectory"></param>
        /// <param name="outputFilename"></param>
        /// <param name="drive"></param>
        /// <param name="system"></param>
        /// <param name="type"></param>
        /// <param name="parameters"></param>
        public DumpEnvironment(Options options,
            string outputDirectory,
            string outputFilename,
            Drive drive,
            RedumpSystem? system,
            MediaType? type,
            string parameters)
        {
            // Set options object
            this.Options = options;

            // Output paths
            (this.OutputDirectory, this.OutputFilename) = InfoTool.NormalizeOutputPaths(outputDirectory, outputFilename, options.InternalProgram == InternalProgram.DiscImageCreator);

            // UI information
            this.Drive = drive;
            this.System = system ?? options.DefaultSystem;
            this.Type = type ?? MediaType.NONE;
            
            // Dumping program
            SetParameters(parameters);
        }

        #region Public Functionality

        /// <summary>
        /// Set the parameters object based on the internal program and parameters string
        /// </summary>
        /// <param name="parameters">String representation of the parameters</param>
        public void SetParameters(string parameters)
        {
            switch (Options.InternalProgram)
            {
                // Dumping support
                case InternalProgram.Aaru:
                    this.Parameters = new Modules.Aaru.Parameters(parameters) { ExecutablePath = Options.AaruPath };
                    break;

                case InternalProgram.DD:
                    this.Parameters = new Modules.DD.Parameters(parameters) { ExecutablePath = Options.DDPath };
                    break;

                case InternalProgram.DiscImageCreator:
                    this.Parameters = new Modules.DiscImageCreator.Parameters(parameters) { ExecutablePath = Options.DiscImageCreatorPath };
                    break;

                // Verification support only
                case InternalProgram.CleanRip:
                    this.Parameters = new Modules.CleanRip.Parameters(parameters) { ExecutablePath = null };
                    break;

                case InternalProgram.DCDumper:
                    this.Parameters = null; // TODO: Create correct parameter type when supported
                    break;

                case InternalProgram.UmdImageCreator:
                    this.Parameters = new Modules.UmdImageCreator.Parameters(parameters) { ExecutablePath = null };
                    break;

                // This should never happen, but it needs a fallback
                default:
                    this.Parameters = new Modules.DiscImageCreator.Parameters(parameters) { ExecutablePath = Options.DiscImageCreatorPath };
                    break;
            }

            // Set system and type
            this.Parameters.System = this.System;
            this.Parameters.Type = this.Type;
        }

        /// <summary>
        /// Get the full parameter string for either DiscImageCreator or Aaru
        /// </summary>
        /// <param name="driveSpeed">Nullable int representing the drive speed</param>
        /// <returns>String representing the params, null on error</returns>
        public string GetFullParameters(int? driveSpeed)
        {
            // Populate with the correct params for inputs (if we're not on the default option)
            if (System != null && Type != MediaType.NONE)
            {
                // If drive letter is invalid, skip this
                if (Drive == null)
                    return null;

                // Set the proper parameters
                string filename = OutputDirectory + Path.DirectorySeparatorChar + OutputFilename;
                switch (Options.InternalProgram)
                {
                    case InternalProgram.Aaru:
                        Parameters = new Modules.Aaru.Parameters(System, Type, Drive.Letter, filename, driveSpeed, Options);
                        break;

                    case InternalProgram.DD:
                        Parameters = new Modules.DD.Parameters(System, Type, Drive.Letter, filename, driveSpeed, Options);
                        break;

                    case InternalProgram.DiscImageCreator:
                        Parameters = new Modules.DiscImageCreator.Parameters(System, Type, Drive.Letter, filename, driveSpeed, Options);
                        break;

                    // This should never happen, but it needs a fallback
                    default:
                        Parameters = new Modules.DiscImageCreator.Parameters(System, Type, Drive.Letter, filename, driveSpeed, Options);
                        break;
                }

                // Generate and return the param string
                return Parameters.GenerateParameters();
            }

            return null;
        }

        #endregion

        #region Dumping

        /// <summary>
        /// Cancel an in-progress dumping process
        /// </summary>
        public void CancelDumping()
        {
            Parameters.KillInternalProgram();
        }

        /// <summary>
        /// Eject the disc using DiscImageCreator
        /// </summary>
        public async void EjectDisc()
        {
            // Validate that the path is configured
            if (string.IsNullOrWhiteSpace(Options.DiscImageCreatorPath))
                return;

            // Validate that the required program exists
            if (!File.Exists(Options.DiscImageCreatorPath))
                return;

            CancelDumping();

            // Validate we're not trying to eject a non-optical
            if (Drive.InternalDriveType != InternalDriveType.Optical)
                return;

            var parameters = new Modules.DiscImageCreator.Parameters(string.Empty)
            {
                BaseCommand = Modules.DiscImageCreator.CommandStrings.Eject,
                DriveLetter = Drive.Letter.ToString(),
                ExecutablePath = Options.DiscImageCreatorPath,
            };

            await ExecuteInternalProgram(parameters);
        }

        /// <summary>
        /// Reset the current drive using DiscImageCreator
        /// </summary>
        public async void ResetDrive()
        {
            // Validate that the path is configured
            if (string.IsNullOrWhiteSpace(Options.DiscImageCreatorPath))
                return;

            // Validate that the required program exists
            if (!File.Exists(Options.DiscImageCreatorPath))
                return;

            // Precautionary check for dumping, just in case
            CancelDumping();

            // Validate we're not trying to reset a non-optical
            if (Drive.InternalDriveType != InternalDriveType.Optical)
                return;

            Modules.DiscImageCreator.Parameters parameters = new Modules.DiscImageCreator.Parameters(string.Empty)
            {
                BaseCommand = Modules.DiscImageCreator.CommandStrings.Reset,
                DriveLetter = Drive.Letter.ToString(),
                ExecutablePath = Options.DiscImageCreatorPath,
            };

            await ExecuteInternalProgram(parameters);
        }

        /// <summary>
        /// Execute the initial invocation of the dumping programs
        /// </summary>
        /// <param name="progress">Optional result progress callback</param>
        public async Task<Result> Run(IProgress<Result> progress = null)
        {
            // Check that we have the basics for dumping
            Result result = IsValidForDump();
            if (!result)
                return result;

            // Invoke output processing, if needed
            if (!Options.ToolsInSeparateWindow)
            {
                outputQueue = new ProcessingQueue<string>(ProcessOutputs);
                Parameters.ReportStatus += OutputToLog;
            }

            // Execute internal tool
            progress?.Report(Result.Success($"Executing {Options.InternalProgram}... {(Options.ToolsInSeparateWindow ? "please wait!" : "see log for output!")}"));
            Directory.CreateDirectory(OutputDirectory);
            await Task.Run(() => Parameters.ExecuteInternalProgram(Options.ToolsInSeparateWindow));
            progress?.Report(Result.Success($"{Options.InternalProgram} has finished!"));

            // Execute additional tools
            progress?.Report(Result.Success("Running any additional tools... see log for output!"));
            result = await Task.Run(() => ExecuteAdditionalTools());
            progress?.Report(result);

            // Remove event handler if needed
            if (!Options.ToolsInSeparateWindow)
            {
                outputQueue.Dispose();
                Parameters.ReportStatus -= OutputToLog;
            }

            return result;
        }

        /// <summary>
        /// Verify that the current environment has a complete dump and create submission info is possible
        /// </summary>
        /// <param name="resultProgress">Optional result progress callback</param>
        /// <param name="protectionProgress">Optional protection progress callback</param>
        /// <param name="processUserInfo">Optional user prompt to deal with submsision information</param>
        /// <returns>Result instance with the outcome</returns>
        public async Task<Result> VerifyAndSaveDumpOutput(
            IProgress<Result> resultProgress = null,
            IProgress<ProtectionProgress> protectionProgress = null,
            Func<SubmissionInfo, (bool?, SubmissionInfo)> processUserInfo = null)
        {
            resultProgress?.Report(Result.Success("Gathering submission information... please wait!"));

            // Check to make sure that the output had all the correct files
            (bool foundFiles, List<string> missingFiles) = InfoTool.FoundAllFiles(this.OutputDirectory, this.OutputFilename, this.Parameters, false);
            if (!foundFiles)
            {
                resultProgress.Report(Result.Failure($"There were files missing from the output:\n{string.Join("\n", missingFiles)}"));
                return Result.Failure("Error! Please check output directory as dump may be incomplete!");
            }

            // Extract the information from the output files
            resultProgress?.Report(Result.Success("Extracting output information from output files..."));
            SubmissionInfo submissionInfo = await InfoTool.ExtractOutputInformation(
                this.OutputDirectory,
                this.OutputFilename,
                this.Drive,
                this.System,
                this.Type,
                this.Options,
                this.Parameters,
                resultProgress,
                protectionProgress);
            resultProgress?.Report(Result.Success("Extracting information complete!"));

            // Eject the disc automatically if confugured to
            if (Options.EjectAfterDump == true)
            {
                resultProgress?.Report(Result.Success($"Ejecting disc in drive {Drive.Letter}"));
                EjectDisc();
            }

            // Reset the drive automatically if confugured to
            if (Options.InternalProgram == InternalProgram.DiscImageCreator && Options.DICResetDriveAfterDump)
            {
                resultProgress?.Report(Result.Success($"Resetting drive {Drive.Letter}"));
                ResetDrive();
            }

            // Get user-modifyable information if confugured to
            if (Options.PromptForDiscInformation && processUserInfo != null)
            {
                resultProgress?.Report(Result.Success("Waiting for additional disc information..."));

                bool? filledInfo;
                (filledInfo, submissionInfo) = processUserInfo(submissionInfo);

                if (filledInfo == true)
                    resultProgress?.Report(Result.Success("Additional disc information added!"));
                else
                    resultProgress?.Report(Result.Success("Disc information skipped!"));
            }

            // Format the information for the text output
            resultProgress?.Report(Result.Success("Formatting information..."));
            List<string> formattedValues = InfoTool.FormatOutputData(submissionInfo);
            resultProgress?.Report(Result.Success("Formatting complete!"));

            // Write the text output
            resultProgress?.Report(Result.Success("Writing information to !submissionInfo.txt..."));
            bool success = InfoTool.WriteOutputData(this.OutputDirectory, formattedValues);
            if (success)
                resultProgress?.Report(Result.Success("Writing complete!"));
            else
                resultProgress?.Report(Result.Failure("Writing could not complete!"));

            // Write the JSON output, if required
            if (Options.OutputSubmissionJSON)
            {
                resultProgress?.Report(Result.Success("Writing information to !submissionInfo.json.gz..."));
                success = InfoTool.WriteOutputData(this.OutputDirectory, submissionInfo);
                if (success)
                    resultProgress?.Report(Result.Success("Writing complete!"));
                else
                    resultProgress?.Report(Result.Failure("Writing could not complete!"));
            }

            // Conpress the logs, if required
            if (Options.CompressLogFiles)
            {
                resultProgress?.Report(Result.Success("Compressing log files..."));
                success = InfoTool.CompressLogFiles(this.OutputDirectory, this.OutputFilename, this.Parameters);
                if (success)
                    resultProgress?.Report(Result.Success("Compression complete!"));
                else
                    resultProgress?.Report(Result.Failure("Compression could not complete!"));
            }

            resultProgress?.Report(Result.Success("Submission information process complete!"));
            return Result.Success();
        }

        /// <summary>
        /// Checks if the parameters are valid
        /// </summary>
        /// <returns>True if the configuration is valid, false otherwise</returns>
        internal bool ParametersValid()
        {
            bool parametersValid = Parameters.IsValid();
            bool floppyValid = !(Drive.InternalDriveType == InternalDriveType.Floppy ^ Type == MediaType.FloppyDisk);

            // TODO: HardDisk being in the Removable category is a hack, fix this later
            bool removableDiskValid = !((Drive.InternalDriveType == InternalDriveType.Removable || Drive.InternalDriveType == InternalDriveType.HardDisk)
                ^ (Type == MediaType.CompactFlash || Type == MediaType.SDCard || Type == MediaType.FlashDrive || Type == MediaType.HardDisk));

            return parametersValid && floppyValid && removableDiskValid;
        }

        /// <summary>
        /// Run any additional tools given a DumpEnvironment
        /// </summary>
        /// <returns>Result instance with the outcome</returns>
        private Result ExecuteAdditionalTools()
        {
            return Result.Success("No external tools needed!");
        }

        /// <summary>
        /// Run internal program async with an input set of parameters
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns>Standard output from commandline window</returns>
        private async Task<string> ExecuteInternalProgram(BaseParameters parameters)
        {
            Process childProcess;
            string output = await Task.Run(() =>
            {
                childProcess = new Process()
                {
                    StartInfo = new ProcessStartInfo()
                    {
                        FileName = parameters.ExecutablePath,
                        Arguments = parameters.GenerateParameters(),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    },
                };
                childProcess.Start();
                childProcess.WaitForExit(1000);

                // Just in case, we want to push a button 5 times to clear any errors
                for (int i = 0; i < 5; i++)
                    childProcess.StandardInput.WriteLine("Y");

                string stdout = childProcess.StandardOutput.ReadToEnd();
                childProcess.Dispose();
                return stdout;
            });

            return output;
        }

        /// <summary>
        /// Validate the current environment is ready for a dump
        /// </summary>
        /// <returns>Result instance with the outcome</returns>
        private Result IsValidForDump()
        {
            // Validate that everything is good
            if (!ParametersValid())
                return Result.Failure("Error! Current configuration is not supported!");

            // Fix the output paths, just in case
            (OutputDirectory, OutputFilename) = InfoTool.NormalizeOutputPaths(OutputDirectory, OutputFilename, Options.InternalProgram == InternalProgram.DiscImageCreator);

            // Validate that the output path isn't on the dumping drive
            string fullOutputPath = Path.GetFullPath(Path.Combine(OutputDirectory, OutputFilename));
            if (fullOutputPath[0] == Drive.Letter)
                return Result.Failure($"Error! Cannot output to same drive that is being dumped!");

            // Validate that the required program exists
            if (!File.Exists(Parameters.ExecutablePath))
                return Result.Failure($"Error! {Parameters.ExecutablePath} does not exist!");

            // Validate that the dumping drive doesn't contain the executable
            string fullExecutablePath = Path.GetFullPath(Parameters.ExecutablePath);
            if (fullExecutablePath[0] == Drive.Letter)
                return Result.Failure("$Error! Cannot dump same drive that executable resides on!");

            // Validate that the current configuration is supported
            return Tools.GetSupportStatus(System, Type);
        }

        #endregion
    }
}
