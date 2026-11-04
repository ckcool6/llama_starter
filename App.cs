using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace MyMarkupApp;

public class App : Application, IXamlMetadataProvider
{
    private Window? _window;
    private readonly XamlControlsXamlMetaDataProvider _provider = new();

    public IXamlType GetXamlType(Type type) => _provider.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _provider.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _provider.GetXmlnsDefinitions();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 加载默认控件样式
        Resources.MergedDictionaries.Add(new XamlControlsResources());

        _window = new MainWindow();
        _window.Activate();
    }
}