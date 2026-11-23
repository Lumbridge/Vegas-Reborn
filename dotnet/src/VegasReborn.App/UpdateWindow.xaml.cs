using System.ComponentModel;
using System.Windows;
using VegasReborn.Core.Update;

namespace VegasReborn.App;

/// <summary>Progress for the install helper started by the previous version (--apply-update).</summary>
public partial class UpdateWindow : Window
{
    private readonly StagedUpdateHelper helper;

    public UpdateWindow(StagedUpdateHelper helper)
    {
        InitializeComponent();
        this.helper = helper;
        helper.ProgressChanged += (_, progress) =>
        {
            StatusText.Text = progress.Message;
            Bar.IsIndeterminate = progress.Total <= 0;
            if (progress.Total > 0)
            {
                Bar.Value = 100.0 * progress.Completed / progress.Total;
            }
        };
    }

    /// <summary>Runs the helper and returns its exit code. Failures are shown before returning.</summary>
    public async Task<int> RunAsync()
    {
        Show();
        var code = await helper.RunAsync();
        if (helper.State == UpdateHelperState.Failed)
        {
            MessageBox.Show(this, helper.ErrorMessage ?? "The update could not finish.", StagedUpdateHelper.FailureTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Close();
        return code;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Files may be mid-replacement; the helper decides when the window can close.
        e.Cancel = !helper.CanClose;
        base.OnClosing(e);
    }
}
