namespace VegasReborn.Core.Update;

public enum UpdateHelperState
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// The visible install helper (the 0.4.x <c>update_ui.run_update</c>) without presentation.
/// The App shows a small, non-closable "Updating Vegas Reborn" window bound to
/// <see cref="Status"/>/<see cref="ProgressChanged"/> with an indeterminate bar, shows
/// <see cref="ErrorMessage"/> in an error dialog titled "Update not completed" on failure,
/// and exits with the code <see cref="RunAsync"/> returns.
/// </summary>
/// <remarks>
/// Events are raised on the <see cref="SynchronizationContext"/> current when <see cref="RunAsync"/>
/// is called (the UI thread), or inline when there is none.
/// </remarks>
public sealed class StagedUpdateHelper
{
    public const string Title = "Updating Vegas Reborn";
    public const string FailureTitle = "Update not completed";
    internal const string GenericFailure = "The update could not finish. Open Vegas Reborn again to retry.";

    private readonly Action<IProgress<UpdateProgress>> _apply;

    public StagedUpdateHelper(string planPath, int parentProcessId)
        : this(progress => StagedUpdate.Apply(planPath, parentProcessId, progress))
    {
        PlanPath = planPath;
        ParentProcessId = parentProcessId;
    }

    internal StagedUpdateHelper(Action<IProgress<UpdateProgress>> apply)
    {
        _apply = apply;
    }

    public string PlanPath { get; } = "";
    public int ParentProcessId { get; }
    public UpdateHelperState State { get; private set; } = UpdateHelperState.Running;
    public string Status { get; private set; } = "Preparing the update...";
    public UpdateProgress? Progress { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>The helper must not be cancelled or closed while it may be replacing files.</summary>
    public bool CanClose => State != UpdateHelperState.Running;

    public event EventHandler<UpdateProgress>? ProgressChanged;

    /// <summary>Raised once with the final <see cref="State"/>.</summary>
    public event EventHandler? Finished;

    /// <summary>Runs the installation on a worker thread. Returns the process exit code: 0 installed, 1 not.</summary>
    public async Task<int> RunAsync()
    {
        var context = SynchronizationContext.Current;
        void Raise(Action action)
        {
            if (context is null)
            {
                action();
            }
            else
            {
                context.Post(_ => action(), null);
            }
        }
        var progress = new InlineProgress<UpdateProgress>(report => Raise(() =>
        {
            Progress = report;
            Status = report.Message;
            ProgressChanged?.Invoke(this, report);
        }));
        string? error = null;
        try
        {
            await Task.Run(() => _apply(progress)).ConfigureAwait(context is not null);
        }
        catch (UpdateException failure)
        {
            error = failure.Message;
        }
        catch (Exception)
        {
            error = GenericFailure;
        }
        // This continuation runs on the captured context after every progress report posted
        // before it, so the final state is never overwritten and is set when the task completes.
        if (error is null)
        {
            State = UpdateHelperState.Succeeded;
        }
        else
        {
            State = UpdateHelperState.Failed;
            Status = "Update not completed.";
            ErrorMessage = error;
        }
        Finished?.Invoke(this, EventArgs.Empty);
        return error is null ? 0 : 1;
    }
}
