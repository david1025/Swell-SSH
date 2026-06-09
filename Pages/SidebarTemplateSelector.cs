using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SwellSSH.Pages
{
    public class SidebarTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? GroupTemplate { get; set; }
        public DataTemplate? ItemTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
        {
            if (item is ConnectionGroupViewModel) return GroupTemplate;
            if (item is ConnectionItemViewModel) return ItemTemplate;
            return base.SelectTemplateCore(item);
        }

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }
}
