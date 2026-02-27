using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GodMode.Avalonia.Views;

public partial class ProjectView : UserControl
{
	private bool _isInitialLoad = true;
	private bool _isAtBottom = true;

	public ProjectView()
	{
		InitializeComponent();

		DataContextChanged += (_, _) =>
		{
			if (DataContext is ProjectViewModel vm)
			{
				vm.OutputMessages.CollectionChanged += OnOutputMessagesChanged;
			}
		};
	}

	private void OnOutputMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (DataContext is not ProjectViewModel vm) return;

		if (e.Action == NotifyCollectionChangedAction.Reset)
		{
			_isInitialLoad = true;
			_isAtBottom = true;
			return;
		}

		if (e.Action == NotifyCollectionChangedAction.Add)
		{
			if (_isAtBottom || _isInitialLoad)
			{
				if (_isInitialLoad && vm.OutputMessages.Count > 5)
					_isInitialLoad = false;

				Dispatcher.UIThread.Post(() =>
				{
					OutputScrollViewer.ScrollToEnd();
				}, DispatcherPriority.Background);
			}
		}
	}

	protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);

		if (DataContext is ProjectViewModel vm)
		{
			vm.OutputMessages.CollectionChanged -= OnOutputMessagesChanged;
			vm.Dispose();
		}
	}
}
