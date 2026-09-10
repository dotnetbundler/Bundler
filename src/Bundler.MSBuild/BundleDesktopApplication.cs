using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotNet.Bundler.MSBuild;

public sealed class BundleDesktopApplication : Microsoft.Build.Utilities.Task
{
    [Required]
    public string DriverPath { get; set; } = "";

    [Required]
    public string ProductName { get; set; } = "";

    [Required]
    public string Identifier { get; set; } = "";

    [Required]
    public string Version { get; set; } = "";

    public string Publisher { get; set; } = "";

    [Required]
    public string RuntimeIdentifier { get; set; } = "";

    [Required]
    public string InputDirectory { get; set; } = "";

    [Required]
    public string OutputDirectory { get; set; } = "";

    [Required]
    public string MainExecutable { get; set; } = "";

    [Required]
    public string Format { get; set; } = "";

    [Required]
    public string ToolArchivePath { get; set; } = "";

    [Required]
    public string ToolCacheDirectory { get; set; } = "";

    [Required]
    public string TemplatePath { get; set; } = "";

    [Required]
    public string IntermediateDirectory { get; set; } = "";

    [Output]
    public string RequestFile { get; private set; } = "";

    public override bool Execute()
    {
        try
        {
            var intermediateDirectory = Path.GetFullPath(IntermediateDirectory);
            Directory.CreateDirectory(intermediateDirectory);
            RequestFile = Path.Combine(intermediateDirectory, "bundle-request.txt");
            WriteRequest(RequestFile);

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "exec " + Quote(DriverPath) + " bundle --request-file " + Quote(RequestFile),
                WorkingDirectory = intermediateDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    Log.LogError("Failed to start the DotNet.Bundler driver.");
                    return false;
                }

                var outputRead = process.StandardOutput.ReadToEndAsync();
                var errorRead = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                System.Threading.Tasks.Task.WaitAll(outputRead, errorRead);

                LogLines(outputRead.Result, isError: false);
                LogLines(errorRead.Result, isError: true);
                if (process.ExitCode != 0)
                {
                    Log.LogError("DotNet.Bundler exited with code {0}.", process.ExitCode);
                    return false;
                }
            }

            return !Log.HasLoggedErrors;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    private void WriteRequest(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["product-name"] = ProductName,
            ["identifier"] = Identifier,
            ["version"] = Version,
            ["publisher"] = Publisher,
            ["rid"] = RuntimeIdentifier,
            ["input"] = Path.GetFullPath(InputDirectory),
            ["output"] = Path.GetFullPath(OutputDirectory),
            ["main-executable"] = MainExecutable,
            ["format"] = Format,
            ["tool-archive"] = Path.GetFullPath(ToolArchivePath),
            ["tool-cache"] = Path.GetFullPath(ToolCacheDirectory),
            ["template"] = Path.GetFullPath(TemplatePath)
        };

        var lines = new List<string> { "DotNet.Bundler.Request.v1" };
        foreach (var pair in values)
        {
            lines.Add(pair.Key + "=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value ?? "")));
        }

        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void LogLines(string text, bool isError)
    {
        using (var reader = new StringReader(text))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (isError)
                {
                    Log.LogError(line);
                }
                else
                {
                    Log.LogMessage(MessageImportance.High, line);
                }
            }
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
