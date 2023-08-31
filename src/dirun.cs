// Copyright (C) 2023 Jefferson Montgomery
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text;

using dirunlib;
using CConsole      = dirunlib.ColorConsole;
using DirectoryInfo = dirunlib.Dirun.DirectoryInfo;
using FileInfo      = dirunlib.Dirun.FileInfo;

namespace dirun;

internal class Program
{
    static void PrintUsage()
    {
        Console.WriteLine("usage: dirun.exe DIR FILES [options] -- COMMAND");
        Console.WriteLine("options:");
        Console.WriteLine("    DIR             The directory to search for files in (default=.)");
        Console.WriteLine("    FILES           Which files to select as targets (default=*.*)");
        Console.WriteLine("    COMMAND         The command to run on each target file found (default=file list)");
        Console.WriteLine("    --working=DIR   The directory to run the command from (default=.)");
        Console.WriteLine("    --pass=NUM      The exit code that command returns when it completes successfully (default=0)");
        Console.WriteLine("    --norecurse     Don't recurse into subdirectories");
        Console.WriteLine("    --repfile       Report status using the target file name, rather than the command");
        Console.WriteLine("    --verbose       Print extra information during execution");
        Console.WriteLine($"build: {GVT.COMMIT}");
    }

    public static int Main(string[] args)
    {
        // Pick console colors
        CConsole.Initialize();

        // Parse command line arguments
        if (args.Length == 0) {
            PrintUsage();
            Environment.Exit(1);
        }

        Dirun dirun = new Dirun();

        string dirPath = Environment.CurrentDirectory;
        string files = "*.*";
        int passCode = 0;
        bool verbose = false;
        bool repFile = false;
        bool dirSet = false;
        bool filesSet = false;
        for (int i = 0; i < args.Length; ++i) {
            var arg = args[i];

            if (arg.StartsWith("--")) {
                if (arg.Length == 2) {
                    if (i + 1 < args.Length) {
                        dirun.Command = new Command(args, i + 1, args.Length);
                    }
                    break;
                }

                if (string.Compare(arg, 2, "working=", 0, 8, false) == 0) {
                    dirun.WorkingDirectory = arg.Substring(2 + 8);
                    if (!Dirun.ContainsVariables(dirun.WorkingDirectory) && !Directory.Exists(dirun.WorkingDirectory)) {
                        CConsole.WriteLineError(CConsole.Red, $"error: invalid working value: {dirun.WorkingDirectory}");
                        PrintUsage();
                        Environment.Exit(1);
                    }
                    continue;
                }

                if (string.Compare(arg, 2, "norecurse", 0, 10, false) == 0) {
                    dirun.Recurse = false;
                    continue;
                }

                if (string.Compare(arg, 2, "repfile", 0, 7, false) == 0) {
                    repFile = true;
                    continue;
                }

                if (string.Compare(arg, 2, "verbose", 0, 7, false) == 0) {
                    verbose = true;
                    continue;
                }

                if (string.Compare(arg, 2, "help", 0, 4, false) == 0 ||
                    string.Compare(arg, 2, "h",    0, 1, false) == 0 ||
                    arg == "?") {
                    PrintUsage();
                    Environment.Exit(0);
                }
            } else {
                if (!dirSet && Directory.Exists(arg)) {
                    dirPath = arg;
                    dirSet = true;
                    continue;
                }

                if (!filesSet) {
                    files = arg;
                    filesSet = true;
                    continue;
                }
            }

            Console.ForegroundColor = CConsole.Red;
            Console.Error.Write($"error: invalid argument: {arg}");
            if (filesSet && !dirSet) {
                Console.Error.Write($" (or, {files} is not a valid directory)");
            }
            Console.Error.WriteLine();
            Console.ResetColor();
            PrintUsage();
            Environment.Exit(1);
        }

        if (dirPath.EndsWith(Path.DirectorySeparatorChar)) {
            dirPath = dirPath.TrimEnd(Path.DirectorySeparatorChar);
        }

        if (verbose) {
            Console.ForegroundColor = CConsole.Gray;
            Console.WriteLine($"dir       = {dirPath}");
            Console.WriteLine($"files     = {files}");
            if (dirun.Recurse) {
                Console.WriteLine("recurse   = true");
            } else {
                Console.WriteLine("recurse   = false");
            }
            Console.WriteLine($"working   = {dirun.WorkingDirectory}");
            if (dirun.Command == null) {
                Console.WriteLine("command   = <none specified>");
            } else {
                Console.WriteLine($"command   = {dirun.Command.ToString()}");
            }
            Console.WriteLine($"pass      = {passCode}");
            Console.WriteLine($"repfile   = {repFile}");
            Console.WriteLine("verbose   = true");
            Console.ResetColor();
        }

        if (dirun.Command == null && dirun.WorkingDirectory.Length > 0) {
            CConsole.WriteLineError(CConsole.Yellow, "warning: working directory not used without a command.");
        }

        int numPassed = 0;
        int numFailed = 0;
        if (dirun.Command != null) {
            if (verbose) {
                dirun.CollectStdout = true;
                dirun.CollectStderr = true;
            }

            var resultSemaphore = new SemaphoreSlim(1);
            dirun.OnFileCompleted = fileInfo =>
            {
                string statusText;
                ConsoleColor statusColor;
                if (fileInfo.Completed && fileInfo.ExitCode == passCode) {
                    statusText = "PASS";
                    statusColor = CConsole.Green;
                    Interlocked.Increment(ref numPassed);
                } else {
                    statusText = "FAIL";
                    statusColor = CConsole.Red;
                }

                string cmd;
                if (verbose || !repFile) {
                    var sb = new StringBuilder(256);
                    if (dirun.WorkingDirectory.Length > 0) {
                        sb.Append("pushd \"");
                        sb.Append(Dirun.ReplaceVariables(dirun.WorkingDirectory, fileInfo));
                        sb.Append("\" && ");
                    }
                    sb.Append(Dirun.ReplaceVariables(dirun.Command.ToString(), fileInfo));
                    if (dirun.WorkingDirectory.Length > 0) {
                        sb.Append(" & popd");
                    }
                    cmd = sb.ToString();
                } else {
                    cmd = string.Empty;
                }

                resultSemaphore.Wait();

                Console.ForegroundColor = statusColor;
                if (verbose) {
                    Console.WriteLine(cmd);
                    if (fileInfo.Stdout.Length > 0) {
                        Console.ForegroundColor = CConsole.Gray;
                        Console.WriteLine(fileInfo.Stdout);
                    }
                    if (fileInfo.Stderr.Length > 0) {
                        Console.ForegroundColor = CConsole.Yellow;
                        Console.WriteLine(fileInfo.Stderr);
                    }
                    Console.ForegroundColor = CConsole.Gray;
                    Console.WriteLine($"exit code = {fileInfo.ExitCode}");
                } else if (repFile) {
                    Console.WriteLine($"{statusText}: {fileInfo.Path}");
                } else {
                    Console.WriteLine($"{statusText}: {cmd}");
                }
                Console.ResetColor();

                resultSemaphore.Release();
            };
        }

        // Start traversing the root diretory looking for target files
        var watch = Stopwatch.StartNew();

        CancellationToken cancel = default; // TODO: timeout

        var dirInfo = new DirectoryInfo();

        try {
            dirun.TraverseDirectory(dirPath, files, dirInfo, cancel);
        }
        catch (Exception e) {
            CConsole.WriteLineError(CConsole.Red, e.Message);
            PrintUsage();
            Environment.Exit(1);
        }

        watch.Stop();

        if (dirInfo.NumFiles == 0) {
            CConsole.WriteLine(CConsole.Yellow, "warning: no files found.");
        } else if (dirun.Command == null) {
            CConsole.WriteLineError(CConsole.Yellow, "warning: no executable provided, listing target files.");
            PrintDirectoryListing(dirInfo, 0, new List<string>() { string.Empty });
        } else {
            Console.ForegroundColor = CConsole.Gray;
            Console.WriteLine($"duration  = {watch.ElapsedMilliseconds} ms");
            Console.WriteLine($"num files = {dirInfo.NumFiles}");
            if (dirun.Command != null) {
                numFailed = dirInfo.NumFiles - numPassed;
                Console.WriteLine($"passed    = {numPassed}");
                Console.WriteLine($"failed    = {numFailed}");
            }
            Console.ResetColor();
        }

        return numFailed;
    }

    static void PrintDirectoryListing(DirectoryInfo dirInfo, int level, List<string> indents)
    {
        int nextLevel = level + 1;
        while (nextLevel >= indents.Count) {
            indents.Add(new string(' ', indents.Count * 2));
        }

        CConsole.WriteLine(CConsole.Blue, $"{indents[level]}{dirInfo.Name}\\");

        foreach (var file in dirInfo.Files) {
            Console.WriteLine($"{indents[nextLevel]}{file.Name}");
        }

        foreach (var subdir in dirInfo.Dirs) {
            if (subdir.NumFiles > 0) {
                PrintDirectoryListing(subdir, nextLevel, indents);
            }
        }
    }
}
