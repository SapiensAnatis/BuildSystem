﻿namespace ModTools;

internal sealed class ExceptionHandlerFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(
        ConsoleAppContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            ConsoleApp.LogError($"Failed to open file at {ex.FileName}.");
            Environment.ExitCode = 1;
        }
#pragma warning disable CA1031 // Do not catch general exception types. App is about to exit at this point; we catch only to avoid dumping a stack trace in release mode.
        catch (Exception ex)
#pragma warning restore CA1031
        {
#if DEBUG
            string exceptionMessage = ex.ToString();
#else
            string exceptionMessage = ex.Message;
#endif
            ConsoleApp.LogError($"Unhandled exception: {exceptionMessage}");
            Environment.ExitCode = 1;
        }
    }
}
