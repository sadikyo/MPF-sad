﻿using System.IO;
using MPF.Core.Data;
using MPF.Library;
using RedumpLib.Data;
using Xunit;

namespace MPF.Test
{
    public class DumpEnvironmentTest
    {
        [Theory]
        [InlineData(null, 'D', false, MediaType.NONE, false)]
        [InlineData("", 'D', false, MediaType.NONE, false)]
        [InlineData("cd F test.bin 8 /c2 20", 'F', false, MediaType.CDROM, true)]
        [InlineData("fd A test.img", 'A', true, MediaType.FloppyDisk, true)]
        [InlineData("dvd X test.iso 8 /raw", 'X', false, MediaType.FloppyDisk, false)]
        [InlineData("stop D", 'D', false, MediaType.DVD, true)]
        public void ParametersValidTest(string parameters, char letter, bool isFloppy, MediaType? mediaType, bool expected)
        {
            var options = new Options() { InternalProgram = InternalProgram.DiscImageCreator };
            var drive = isFloppy
                ? new Drive(InternalDriveType.Floppy, new DriveInfo(letter.ToString()))
                : new Drive(InternalDriveType.Optical, new DriveInfo(letter.ToString()));

            var env = new DumpEnvironment(options, string.Empty, string.Empty, drive, RedumpSystem.IBMPCcompatible, mediaType, parameters);

            bool actual = env.ParametersValid();
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(null, null, null, null)]
        [InlineData(" ", "", " ", "")]
        [InlineData("super", "blah.bin", "super", "blah.bin")]
        [InlineData("super\\hero", "blah.bin", "super\\hero", "blah.bin")]
        [InlineData("super.hero", "blah.bin", "super.hero", "blah.bin")]
        [InlineData("superhero", "blah.rev.bin", "superhero", "blah.rev.bin")]
        [InlineData("super&hero", "blah.bin", "super&hero", "blah.bin")]
        [InlineData("superhero", "blah&foo.bin", "superhero", "blah&foo.bin")]
        public void FixOutputPathsTest(string outputDirectory, string outputFilename, string expectedOutputDirectory, string expectedOutputFilename)
        {
            (string actualOutputDirectory, string actualOutputFilename) = InfoTool.NormalizeOutputPaths(outputDirectory, outputFilename, false);
            Assert.Equal(expectedOutputDirectory, actualOutputDirectory);
            Assert.Equal(expectedOutputFilename, actualOutputFilename);
        }

        [Fact]
        public void GetFirstTrackTest()
        {
            // TODO: Implement
            Assert.True(true);
        }

        [Fact]
        public void FormatOutputDataTest()
        {
            // TODO: Implement
            Assert.True(true);
        }

        [Fact]
        public void WriteOutputDataTest()
        {
            // TODO: Implement
            Assert.True(true);
        }

        [Fact]
        public void EjectDiscTest()
        {
            // TODO: Implement
            Assert.True(true);
        }

        [Fact]
        public void CancelDumpingTest()
        {
            // TODO: Implement
            Assert.True(true);
        }

        [Fact]
        public void StartDumpingTest()
        {
            // TODO: Implement
            Assert.True(true);
        }
    }
}
