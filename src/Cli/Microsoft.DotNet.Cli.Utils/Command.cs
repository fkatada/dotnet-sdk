﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.DotNet.Cli.Utils.Extensions;

namespace Microsoft.DotNet.Cli.Utils;

public class Command(Process? process, bool trimTrailingNewlines = false, IDictionary<string, string?>? customEnvironmentVariables = null) : ICommand
{
    private readonly Process _process = process ?? throw new ArgumentNullException(nameof(process));

    private readonly Dictionary<string, string?>? _customEnvironmentVariables =
        // copy the dictionary to avoid mutating the original
        customEnvironmentVariables == null ? null : new(customEnvironmentVariables);

    private StreamForwarder? _stdOut;

    private StreamForwarder? _stdErr;

    private bool _running = false;

    private bool _trimTrailingNewlines = trimTrailingNewlines;

    public CommandResult Execute()
    {
        return Execute(null);
    }
    public CommandResult Execute(Action<Process>? processStarted)
    {
        Reporter.Verbose.WriteLine(string.Format(
            LocalizableStrings.RunningFileNameArguments,
            _process.StartInfo.FileName,
            _process.StartInfo.Arguments));

        ThrowIfRunning();

        _running = true;

        _process.EnableRaisingEvents = true;

        Stopwatch? sw = null;
        if (CommandLoggingContext.IsVerbose)
        {
            sw = Stopwatch.StartNew();

            Reporter.Verbose.WriteLine($"> {Command.FormatProcessInfo(_process.StartInfo)}".White());
        }

        using (var reaper = new ProcessReaper(_process))
        {
            _process.Start();
            processStarted?.Invoke(_process);
            reaper.NotifyProcessStarted();

            Reporter.Verbose.WriteLine(string.Format(
                LocalizableStrings.ProcessId,
                _process.Id));

            var taskOut = _stdOut?.BeginRead(_process.StandardOutput);
            var taskErr = _stdErr?.BeginRead(_process.StandardError);
            _process.WaitForExit();

            taskOut?.Wait();
            taskErr?.Wait();
        }

        var exitCode = _process.ExitCode;

        if (CommandLoggingContext.IsVerbose)
        {
            Debug.Assert(sw is not null);
            var message = string.Format(
                LocalizableStrings.ProcessExitedWithCode,
                Command.FormatProcessInfo(_process.StartInfo),
                exitCode,
                sw.ElapsedMilliseconds);
            if (exitCode == 0)
            {
                Reporter.Verbose.WriteLine(message.Green());
            }
            else
            {
                Reporter.Verbose.WriteLine(message.Red().Bold());
            }
        }

        return new CommandResult(
            _process.StartInfo,
            exitCode,
            _stdOut?.CapturedOutput,
            _stdErr?.CapturedOutput);
    }

    public ICommand WorkingDirectory(string? projectDirectory)
    {
        _process.StartInfo.WorkingDirectory = projectDirectory;
        return this;
    }

    public ICommand EnvironmentVariable(string name, string? value)
    {
        _process.StartInfo.Environment[name] = value;
        _customEnvironmentVariables?[name] = value;
        return this;
    }

    public ICommand StandardOutputEncoding(Encoding encoding)
    {
        _process.StartInfo.StandardOutputEncoding = encoding;
        return this;
    }

    public ICommand CaptureStdOut()
    {
        ThrowIfRunning();
        EnsureStdOut();
        _stdOut?.Capture(_trimTrailingNewlines);
        return this;
    }

    public ICommand CaptureStdErr()
    {
        ThrowIfRunning();
        EnsureStdErr();
        _stdErr?.Capture(_trimTrailingNewlines);
        return this;
    }

    public ICommand ForwardStdOut(TextWriter? to = null, bool onlyIfVerbose = false, bool ansiPassThrough = true)
    {
        ThrowIfRunning();
        if (!onlyIfVerbose || CommandLoggingContext.IsVerbose)
        {
            EnsureStdOut();

            if (to == null)
            {
                _stdOut?.ForwardTo(writeLine: Reporter.Output.WriteLine);
                EnvironmentVariable(CommandLoggingContext.Variables.AnsiPassThru, ansiPassThrough.ToString());
            }
            else
            {
                _stdOut?.ForwardTo(writeLine: to.WriteLine);
            }
        }
        return this;
    }

    public ICommand ForwardStdErr(TextWriter? to = null, bool onlyIfVerbose = false, bool ansiPassThrough = true)
    {
        ThrowIfRunning();
        if (!onlyIfVerbose || CommandLoggingContext.IsVerbose)
        {
            EnsureStdErr();

            if (to == null)
            {
                _stdErr?.ForwardTo(writeLine: Reporter.Error.WriteLine);
                EnvironmentVariable(CommandLoggingContext.Variables.AnsiPassThru, ansiPassThrough.ToString());
            }
            else
            {
                _stdErr?.ForwardTo(writeLine: to.WriteLine);
            }
        }
        return this;
    }

    public ICommand OnOutputLine(Action<string> handler)
    {
        ThrowIfRunning();
        EnsureStdOut();

        _stdOut?.ForwardTo(writeLine: handler);
        return this;
    }

    public ICommand OnErrorLine(Action<string> handler)
    {
        ThrowIfRunning();
        EnsureStdErr();

        _stdErr?.ForwardTo(writeLine: handler);
        return this;
    }

    public string CommandName => _process.StartInfo.FileName;

    public string CommandArgs => _process.StartInfo.Arguments;

    public ProcessStartInfo StartInfo => _process.StartInfo;

    /// <summary>
    /// If set in the constructor, it's used to keep track of environment variables modified via <see cref="EnvironmentVariable"/>
    /// unlike <see cref="ProcessStartInfo.Environment"/> which includes all environment variables of the current process.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? CustomEnvironmentVariables => _customEnvironmentVariables;

    public ICommand SetCommandArgs(string commandArgs)
    {
        _process.StartInfo.Arguments = commandArgs;
        return this;
    }

    private static string FormatProcessInfo(ProcessStartInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Arguments))
        {
            return info.FileName;
        }

        return info.FileName + " " + info.Arguments;
    }

    private void EnsureStdOut()
    {
        _stdOut ??= new StreamForwarder();
        _process.StartInfo.RedirectStandardOutput = true;
    }

    private void EnsureStdErr()
    {
        _stdErr ??= new StreamForwarder();
        _process.StartInfo.RedirectStandardError = true;
    }

    private void ThrowIfRunning([CallerMemberName] string? memberName = null)
    {
        if (_running)
        {
            throw new InvalidOperationException(string.Format(
                LocalizableStrings.UnableToInvokeMemberNameAfterCommand,
                memberName));
        }
    }
}
