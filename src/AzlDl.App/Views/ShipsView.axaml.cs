using Avalonia.Controls;
using AzlDl.App.ViewModels;

namespace AzlDl.App.Views;

public partial class ShipsView : UserControl
{
    public ShipsView()
    {
        InitializeComponent();

        // The gallery lays cards out in rows; as many cards as fit the width go in each row.
        CardList.SizeChanged += (_, e) =>
        {
            if (DataContext is ShipsViewModel ships && e.NewSize.Width > 0)
            {
                var room = e.NewSize.Width - CardList.Padding.Left - CardList.Padding.Right - 16;
                ships.Columns = Math.Max(1, (int)((room + ShipsViewModel.CardSpacing) / (ShipsViewModel.CardWidth + ShipsViewModel.CardSpacing)));
            }
        };
    }
}
