using System.Collections.Specialized;
using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class ProjectPage : ContentPage
{
    private CancellationTokenSource? _scrollDebounce;

    public ProjectPage(ProjectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        // Subscribe to collection changes to auto-scroll to bottom
        viewModel.OutputMessages.CollectionChanged += OnOutputMessagesChanged;
    }

    private void OnOutputMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && BindingContext is ProjectViewModel vm)
        {
            // Debounce scroll - cancel previous pending scroll and schedule a new one
            _scrollDebounce?.Cancel();
            _scrollDebounce = new CancellationTokenSource();
            var token = _scrollDebounce.Token;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    // Wait for more messages to arrive before scrolling
                    await Task.Delay(100, token);

                    if (!token.IsCancellationRequested && vm.OutputMessages.Count > 0)
                    {
                        OutputCollectionView.ScrollTo(vm.OutputMessages.Count - 1, position: ScrollToPosition.End, animate: false);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Expected when debouncing - a new scroll was scheduled
                }
            });
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

        _scrollDebounce?.Cancel();
        _scrollDebounce?.Dispose();

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
