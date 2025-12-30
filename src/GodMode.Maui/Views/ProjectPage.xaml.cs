using System.Collections.Specialized;
using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class ProjectPage : ContentPage
{
    private bool _isInitialLoad = true;
    private bool _isAtBottom = true;

    public ProjectPage(ProjectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        viewModel.OutputMessages.CollectionChanged += OnOutputMessagesChanged;
        OutputScrollView.Scrolled += OnScrolled;
    }

    private void OnScrolled(object? sender, ScrolledEventArgs e)
    {
        // Check if user is at or near bottom (within 50 pixels)
        var scrollView = OutputScrollView;
        var distanceFromBottom = scrollView.ContentSize.Height - scrollView.ScrollY - scrollView.Height;
        _isAtBottom = distanceFromBottom < 50;
    }

    private void OnOutputMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (BindingContext is not ProjectViewModel vm) return;

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _isInitialLoad = true;
            _isAtBottom = true;
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // Auto-scroll if at bottom or during initial load
            if (_isAtBottom || _isInitialLoad)
            {
                if (_isInitialLoad && vm.OutputMessages.Count > 5)
                {
                    _isInitialLoad = false;
                }

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Task.Delay(50); // Let layout complete
                    await OutputScrollView.ScrollToAsync(0, double.MaxValue, false);
                });
            }
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is ProjectViewModel viewModel)
        {
            await viewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        OutputScrollView.Scrolled -= OnScrolled;

        if (BindingContext is ProjectViewModel viewModel)
        {
            viewModel.OutputMessages.CollectionChanged -= OnOutputMessagesChanged;
            viewModel.Dispose();
        }
    }

    private async void OnInputEditorCompleted(object? sender, EventArgs e)
    {
        if (BindingContext is ProjectViewModel viewModel && viewModel.SendInputCommand.CanExecute(null))
        {
            await viewModel.SendInputCommand.ExecuteAsync(null);
        }
    }
}
