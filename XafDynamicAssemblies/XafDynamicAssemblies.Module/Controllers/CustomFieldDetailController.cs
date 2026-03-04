using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Controllers
{
    public class CustomFieldDetailController : ObjectViewController<DetailView, CustomField>
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            var typeNameItem = View.FindItem("TypeName") as PropertyEditor;
            if (typeNameItem != null)
            {
                typeNameItem.ControlCreated += TypeNameItem_ControlCreated;
            }
        }

        private void TypeNameItem_ControlCreated(object sender, EventArgs e)
        {
            if (sender is PropertyEditor editor)
            {
                var model = editor.Model as IModelCommonMemberViewItem;
                if (model != null)
                {
                    model.PredefinedValues = string.Join(";", SupportedTypes.AllTypeNames);
                }
            }
        }

        protected override void OnDeactivated()
        {
            var typeNameItem = View.FindItem("TypeName") as PropertyEditor;
            if (typeNameItem != null)
            {
                typeNameItem.ControlCreated -= TypeNameItem_ControlCreated;
            }
            base.OnDeactivated();
        }
    }
}
