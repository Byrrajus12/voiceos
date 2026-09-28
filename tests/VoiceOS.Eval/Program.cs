using Microsoft.Extensions.Logging;
using VoiceOS.Core.Config;
using VoiceOS.Core.Speech;
using VoiceOS.Eval.Evaluation;
using VoiceOS.Eval.Live;

var rawArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (rawArgs.Length > 0 && rawArgs[0] == "live")
    return await LiveEvalCli.RunAsync(rawArgs.Skip(1).ToArray());

DotEnvLoader.Load();

using var loggerFactory = LoggerFactory.Create(b =>
    b.SetMinimumLevel(LogLevel.Warning)
     .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));

var cmdArgs = Environment.GetCommandLineArgs();

if (cmdArgs.Contains("--eval-jev"))
{
    Console.WriteLine("=== Jev Evaluation Harness ===");
    await JevEvaluationHarness.RunAsync(loggerFactory);
    return 0;
}

if (cmdArgs.Contains("--eval-stt"))
{
    var recordingsDir = cmdArgs.SkipWhile(a => a != "--recordings").Skip(1).FirstOrDefault()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VoiceOS", "recordings");

    var modelDir = cmdArgs.SkipWhile(a => a != "--model-dir").Skip(1).FirstOrDefault()
        ?? ModelManager.GetDefaultModelDirectory();

    Console.WriteLine("=== STT Evaluation Harness ===");
    Console.WriteLine($"Recordings: {recordingsDir}");
    Console.WriteLine($"Model: {modelDir}");
    await SttEvaluationHarness.RunAsync(recordingsDir, modelDir, loggerFactory);
    return 0;
}

Console.WriteLine("VoiceOS Evaluation Harness");
Console.WriteLine("Usage:");
Console.WriteLine("  --eval-jev                         Run Jev decision engine evaluation");
Console.WriteLine("  --eval-stt [--recordings <dir>]    Run STT evaluation on WAV files");
Console.WriteLine("                [--model-dir <dir>]");
Console.WriteLine("  live [--scenario <id>]... [--tag <t>]... [--family <f>]... [--all] [--list]");
Console.WriteLine("       [--repeat N] [--include-unsafe] [--scenarios <dir>] [--out <dir>] [--verbose]");
Console.WriteLine("  live summarize <results.jsonl>...  Re-aggregate existing live eval results");
return 0;
