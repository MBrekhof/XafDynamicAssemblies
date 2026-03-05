using DevExpress.ExpressApp.Model;

namespace XafDynamicAssemblies.Module.Editors
{
    /// <summary>
    /// Application Model interface for the AI Chat ViewItem.
    /// The Blazor platform-specific ViewItem class references this interface
    /// via <see cref="DevExpress.ExpressApp.Editors.ViewItemAttribute"/>
    /// so that a single model node name is shared across platforms.
    /// </summary>
    public interface IModelAIChatViewItem : IModelViewItem { }
}
