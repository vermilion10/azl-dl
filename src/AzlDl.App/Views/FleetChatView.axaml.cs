using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using AzlDl.App.ViewModels;

namespace AzlDl.App.Views;

public partial class FleetChatView : UserControl
{
    public FleetChatView()
    {
        InitializeComponent();

        // Like a messaging app, the newest message stays in view: after a reply the chat scrolls to its end.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FleetChatViewModel chat)
            {
                chat.Messages.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add)
                    {
                        Dispatcher.UIThread.Post(() => MessageScroll.ScrollToEnd(), DispatcherPriority.Background);
                    }
                };
            }
        };
    }
}
