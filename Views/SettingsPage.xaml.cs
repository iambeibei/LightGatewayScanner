using LightGatewayScanner.ViewModels;

namespace LightGatewayScanner.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 每次进入设置页都重新载入当前生效配置，避免展示的是过期数据
        _viewModel.LoadFromConfigCommand.Execute(null);
    }
}
