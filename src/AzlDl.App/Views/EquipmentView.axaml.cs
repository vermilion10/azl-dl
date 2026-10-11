using Avalonia.Controls;
using AzlDl.App.ViewModels;

namespace AzlDl.App.Views;

public partial class EquipmentView : UserControl
{
    public EquipmentView()
    {
        InitializeComponent();

        // As many cards as fit the width go in each row.
        CardList.SizeChanged += (_, e) =>
        {
            if (DataContext is EquipmentViewModel equipment && e.NewSize.Width > 0)
            {
                var room = e.NewSize.Width - CardList.Padding.Left - CardList.Padding.Right - 16;
                var card = EquipmentViewModel.CardWidth + 8 + EquipmentViewModel.CardSpacing;
                equipment.Columns = Math.Max(1, (int)((room + EquipmentViewModel.CardSpacing) / card));
            }
        };
    }
}
